using LMP.UI.Dialogs;

namespace LMP.UI.Services;

/// <summary>
/// Централизованный сервис редактирования плейлистов.
///
/// <para><b>Зачем нужен:</b></para>
/// <para>
/// Логика редактирования плейлиста (переименование, обложка, цвет, привязка/отвязка YouTube)
/// ранее дублировалась в <c>LibraryViewModel.EditPlaylistFromCardAsync</c> и
/// <c>PlaylistViewModel.EditPlaylistAsync</c> (~80% одинакового кода).
/// Этот сервис — единый источник истины (Single Source of Truth).
/// </para>
///
/// <para><b>Принцип работы:</b></para>
/// <list type="number">
///   <item>Загружает актуальное состояние плейлиста из БД</item>
///   <item>Показывает диалог редактирования</item>
///   <item>Применяет изменения: sync → name → thumbnail → description → color</item>
///   <item>Сохраняет в БД (что триггерит <c>OnPlaylistChanged</c>)</item>
/// </list>
///
/// <para><b>Зависимости:</b></para>
/// <para>Не знает о UI-страницах. Навигационную блокировку получает через callback-и.</para>
/// </summary>
public sealed class PlaylistEditService
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly PlaylistSyncService _syncService;
    private readonly NotificationService _notifications;

    /// <summary>Быстрый доступ к локализации.</summary>
    private static LocalizationService SL => LocalizationService.Instance;

    /// <param name="library">Сервис библиотеки для чтения/записи данных.</param>
    /// <param name="youtube">Провайдер YouTube API для облачных операций.</param>
    /// <param name="auth">Сервис авторизации для проверки доступа к YouTube.</param>
    /// <param name="syncService">Сервис синхронизации плейлистов.</param>
    /// <param name="notifications">Сервис уведомлений для toast-сообщений.</param>
    /// <param name="dialog">Сервис диалогов для показа UI.</param>
    public PlaylistEditService(
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        PlaylistSyncService syncService,
        NotificationService notifications,
        DialogService dialog)
    {
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _syncService = syncService;
        _notifications = notifications;
        _dialog = dialog;
    }

    /// <summary>
    /// Результат операции редактирования.
    /// </summary>
    /// <param name="Changed">Были ли фактические изменения в плейлисте.</param>
    /// <param name="Playlist">Обновлённый объект плейлиста.</param>
    public sealed record EditResult(bool Changed, Playlist Playlist);

    /// <summary>
    /// Выполняет полный цикл редактирования плейлиста.
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Загружает свежее состояние из БД (чтобы не работать с устаревшими данными)</item>
    ///   <item>Показывает диалог <see cref="EditPlaylistDialogViewModel"/></item>
    ///   <item>Если запрошена копия — делегирует в <see cref="CreateCopyAsync"/></item>
    ///   <item>Обрабатывает изменение синхронизации (link/unlink YouTube)</item>
    ///   <item>Обрабатывает переименование — пропускается для системных плейлистов</item>
    ///   <item>Обрабатывает обложку (валидация URI + локальные пути для мозаик)</item>
    ///   <item>Обрабатывает описание</item>
    ///   <item>Обрабатывает цвет (CustomColor + ComputedColor)</item>
    ///   <item>Сохраняет всё одним вызовом в БД</item>
    /// </list>
    ///
    /// <para><b>Порядок шагов важен:</b> sync toggle обрабатывается ПЕРЕД rename,
    /// потому что после привязки к YouTube переименование должно идти через API.</para>
    /// </summary>
    /// <param name="playlistId">ID плейлиста для редактирования.</param>
    /// <param name="lockNavigation">
    /// Callback для блокировки навигации на время сетевых операций.
    /// </param>
    /// <param name="unlockNavigation">Callback для разблокировки навигации.</param>
    /// <returns>
    /// <see cref="EditResult"/> с флагом изменений и обновлённым плейлистом.
    /// <c>null</c> если пользователь отменил диалог или плейлист не найден.
    /// </returns>
    public async Task<EditResult?> EditPlaylistAsync(
            string playlistId,
            Action<string> lockNavigation,
            Action unlockNavigation)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId);
        if (playlist == null) return null;

        // Mutation guard: read-only плейлисты не редактируются
        if (!playlist.IsEditable)
        {
            var message = !string.IsNullOrEmpty(playlist.Author)
                ? string.Format(
                    SL["Playlist_ReadOnly_ByAuthor"] ?? "Playlist by {0} is read-only",
                    playlist.Author)
                : SL["Playlist_ReadOnly"] ?? "This playlist is read-only";

            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                message);

            return null;
        }

        // Загружаем треки плейлиста для работы вкладки «Из треков» (мозаика)
        var tracks = await _library.GetPlaylistTracksAsync(playlistId);

        var result = await _dialog.ShowEditPlaylistDialogAsync(playlist, tracks);
        if (result == null) return null;

        // CREATE COPY: обрабатываем до всех остальных шагов
        // Создаём новый локальный плейлист с данными из редактора и копируем треки.
        // Оригинальный плейлист остаётся нетронутым.
        if (result.ShouldCreateCopy)
            return await CreateCopyAsync(playlist, result, lockNavigation, unlockNavigation);

        bool changed = false;

        // STEP 1: Sync toggle
        if (result.SyncToCloud.HasValue && result.SyncToCloud.Value != playlist.IsFromAccount)
        {
            bool wantsSync = result.SyncToCloud.Value;

            if (wantsSync && !playlist.IsFromAccount && _auth.IsAuthenticated)
            {
                changed |= await TryLinkToCloudAsync(
                    playlist, playlistId, lockNavigation, unlockNavigation);
            }
            else if (!wantsSync && playlist.IsFromAccount)
            {
                changed |= await TryUnlinkFromCloudAsync(playlist);
            }
        }

        // STEP 2: Rename
        if (!LibraryService.IsSystemPlaylist(playlistId))
        {
            var newName = result.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(newName) &&
                !string.Equals(newName, playlist.Name, StringComparison.Ordinal))
            {
                // Убрана автоматическая отправка на YouTube
                playlist.Name = newName;
                changed = true;
            }
        }

        // STEP 3: Thumbnail
        if (!string.Equals(result.ThumbnailUrl, playlist.ThumbnailUrl, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                if (IsValidThumbnail(result.ThumbnailUrl))
                {
                    playlist.ThumbnailUrl = result.ThumbnailUrl;
                    playlist.ComputedColor = null;
                    changed = true;
                }
                else
                {
                    Log.Warn($"[PlaylistEdit] Invalid thumbnail path: {result.ThumbnailUrl}");

                    await _notifications.ShowToastAsync(
                        titleKey: "Dialog_Warning_Title",
                        messageKey: "Error_InvalidThumbnailUrl",
                        severity: NotificationSeverity.Warning,
                        durationMs: 4000);
                }
            }
            else
            {
                playlist.ThumbnailUrl = null;
                playlist.ComputedColor = null;
                changed = true;
            }
        }

        // STEP 3.5: Description
        if (!string.Equals(result.Description?.Trim(), playlist.Description?.Trim(), StringComparison.Ordinal))
        {
            // Убрана автоматическая отправка на YouTube
            playlist.Description = result.Description?.Trim();
            changed = true;
        }

        // STEP 4: Custom Color
        if (!string.Equals(result.CustomColor, playlist.CustomColor, StringComparison.Ordinal))
        {
            playlist.CustomColor = result.CustomColor;
            changed = true;
        }

        // STEP 4.5: Computed Color
        if (result.ComputedColor != null &&
            !string.Equals(result.ComputedColor, playlist.ComputedColor, StringComparison.OrdinalIgnoreCase))
        {
            playlist.ComputedColor = result.ComputedColor;
            changed = true;
        }

        // STEP 5: Save
        if (changed)
        {
            playlist.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(playlist);

            Log.Info($"[PlaylistEdit] Saved: Id={playlist.Id}, " +
                     $"SyncMode={playlist.SyncMode}, " +
                     $"YoutubeId={playlist.YoutubeId ?? "null"}, " +
                     $"Name={playlist.Name}");

            await _notifications.ShowToastAsync(
                titleKey: "EditPlaylist_Saved",
                messageKey: "EditPlaylist_Saved",
                severity: NotificationSeverity.Success,
                durationMs: 2000);

            NotificationService.PlaySuccessSound();
        }

        return new EditResult(changed, playlist);
    }

    /// <summary>
    /// Создаёт локальную копию плейлиста с данными из редактора.
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Создаёт новый плейлист с данными из редактора (имя, обложка, цвет, описание)</item>
    ///   <item>Копирует все треки из оригинала в новый плейлист</item>
    ///   <item>Сохраняет в БД</item>
    /// </list>
    ///
    /// <para><b>Ограничения:</b> копия всегда локальная, без привязки к YouTube,
    /// независимо от статуса оригинала.</para>
    /// </summary>
    private async Task<EditResult?> CreateCopyAsync(
        Playlist original,
        EditPlaylistResult editorResult,
        Action<string> lockNavigation,
        Action unlockNavigation)
    {
        lockNavigation(SL["Playlist_CreatingCopy"] ?? "Creating copy...");
        try
        {
            var copyName = string.IsNullOrWhiteSpace(editorResult.Name)
                ? original.Name
                : editorResult.Name.Trim();

            // Суффикс добавляем только если имя не менялось
            if (string.Equals(copyName, original.Name, StringComparison.Ordinal))
                copyName = $"{copyName} ({SL["Playlist_CopySuffix"] ?? "copy"})";

            var copy = new Playlist
            {
                Name = copyName,
                ThumbnailUrl = editorResult.ThumbnailUrl,
                CustomColor = editorResult.CustomColor,
                Description = editorResult.Description,
                ComputedColor = editorResult.ComputedColor,
                SyncMode = PlaylistSyncMode.LocalOnly,
                YoutubeId = null,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await _library.AddOrUpdatePlaylistAsync(copy);

            var originalTrackIds = await _library.GetPlaylistTrackIdsAsync(original.Id);
            if (originalTrackIds.Count > 0)
            {
                // Переиспользуем AddOrUpdatePlaylistAsync с TrackIds для батчевой вставки треков
                await _library.AddOrUpdatePlaylistAsync(new Playlist
                {
                    Id = copy.Id,
                    Name = copy.Name,
                    ThumbnailUrl = copy.ThumbnailUrl,
                    CustomColor = copy.CustomColor,
                    Description = copy.Description,
                    ComputedColor = copy.ComputedColor,
                    SyncMode = PlaylistSyncMode.LocalOnly,
                    TrackIds = [.. originalTrackIds],
                    UpdatedAt = DateTime.Now
                });
            }

            Log.Info($"[PlaylistEdit] Copy created: '{copy.Name}' (id={copy.Id}), " +
                     $"tracks={originalTrackIds.Count}, source={original.Id}");

            await _notifications.ShowToastAsync(
                titleKey: "EditPlaylist_CopyCreated",
                messageKey: "EditPlaylist_CopyCreated",
                severity: NotificationSeverity.Success,
                durationMs: 2500);

            NotificationService.PlaySuccessSound();

            return new EditResult(true, copy);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEdit] Copy creation failed: {ex.Message}");

            await _notifications.ShowToastAsync(
                titleKey: "Dialog_Error_Title",
                messageKey: "EditPlaylist_CopyFailed",
                messageArgs: [ex.Message],
                severity: NotificationSeverity.Error,
                durationMs: 4000);

            _notifications.TryPlayErrorSound();
            return null;
        }
        finally
        {
            unlockNavigation();
        }
    }

    /// <summary>
    /// Проверяет валидность значения обложки.
    ///
    /// <para>Допустимые форматы:</para>
    /// <list type="bullet">
    ///   <item>HTTP/HTTPS URL — обложка из интернета</item>
    ///   <item><c>avares://</c> URI — встроенный ресурс приложения</item>
    ///   <item>Абсолютный путь к существующему файлу — мозаика или выбранный файл</item>
    ///   <item><c>file://</c> URI — локальный файл</item>
    /// </list>
    /// </summary>
    private static bool IsValidThumbnail(string? thumbnailValue)
    {
        if (string.IsNullOrWhiteSpace(thumbnailValue)) return false;

        if (thumbnailValue.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            thumbnailValue.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;

        if (thumbnailValue.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (thumbnailValue.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(thumbnailValue, UriKind.Absolute, out var fileUri))
                return File.Exists(fileUri.LocalPath);
            return false;
        }

        if (Path.IsPathRooted(thumbnailValue))
            return File.Exists(thumbnailValue);

        return false;
    }

    /// <summary>
    /// Привязывает локальный плейлист к YouTube Music.
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Блокирует навигацию (показывает спиннер)</item>
    ///   <item>Создаёт пустой плейлист в YouTube через API</item>
    ///   <item>Устанавливает <c>YoutubeId</c> и <c>SyncMode=TwoWaySync</c></item>
    ///   <item>Сохраняет плейлист (чтобы YoutubeId был в БД)</item>
    ///   <item>Запускает синхронизацию треков через PlaylistSyncService</item>
    /// </list>
    /// </summary>
    /// <returns><c>true</c> если привязка успешна.</returns>
    private async Task<bool> TryLinkToCloudAsync(
        Playlist playlist,
        string localPlaylistId,
        Action<string> lockNavigation,
        Action unlockNavigation)
    {
        lockNavigation(SL["Playlist_LinkingToCloud"] ?? "Linking to YouTube Music...");
        try
        {
            var ytId = await _youtube.CreatePlaylistAsync(playlist.Name);

            if (string.IsNullOrEmpty(ytId))
            {
                await _notifications.ShowToastAsync(
                    titleKey: "Dialog_Error_Title",
                    messageKey: "Playlist_CloudCreateFailed",
                    severity: NotificationSeverity.Error,
                    durationMs: 4000);

                _notifications.TryPlayErrorSound();
                return false;
            }

            playlist.YoutubeId = ytId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
            playlist.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(playlist);

            Log.Info($"[PlaylistEdit] Linked to YouTube: {ytId}");

            unlockNavigation();

            var trackIds = await _library.GetPlaylistTrackIdsAsync(localPlaylistId);
            if (trackIds.Count > 0)
            {
                var syncOptions = new PlaylistSyncOptions
                {
                    Strategy = PlaylistSyncStrategy.ReplaceCloud,
                    SyncName = false,
                    SyncDescription = false,
                    SyncThumbnail = false,
                    SyncTracks = true
                };

                var syncResult = await _syncService.SyncDirectAsync(localPlaylistId, syncOptions);

                if (syncResult.Success)
                    Log.Info($"[PlaylistEdit] Tracks synced after link: {syncResult.TracksAddedToCloud}");
                else
                    Log.Warn($"[PlaylistEdit] Track sync after link failed: {syncResult.ErrorMessage}");
            }

            await _notifications.ShowToastAsync(
                titleKey: "EditPlaylist_Linked",
                messageKey: "EditPlaylist_Linked",
                severity: NotificationSeverity.Success,
                durationMs: 3000);

            NotificationService.PlaySuccessSound();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEdit] Cloud link failed: {ex.Message}");

            await _notifications.ShowToastAsync(
                titleKey: "Dialog_Error_Title",
                messageKey: "Playlist_CloudLinkFailed",
                messageArgs: [ex.Message],
                severity: NotificationSeverity.Error,
                durationMs: 5000);

            _notifications.TryPlayErrorSound();

            return false;
        }
        finally
        {
            unlockNavigation();
        }
    }

    /// <summary>
    /// Отвязывает плейлист от YouTube Music.
    ///
    /// <para><b>Важно:</b> плейлист НЕ удаляется из YouTube-аккаунта,
    /// просто локальная копия перестаёт синхронизироваться.</para>
    /// </summary>
    /// <returns><c>true</c> если пользователь подтвердил отвязку.</returns>
    private async Task<bool> TryUnlinkFromCloudAsync(Playlist playlist)
    {
        var confirm = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"] ?? "Confirm",
            SL["Playlist_UnlinkConfirm"]
                ?? "Unlink this playlist from YouTube Music?\n\n" +
                   "The playlist will remain in your YouTube account, " +
                   "but local changes will no longer sync.",
            SL["Playlist_Unlink"] ?? "Unlink",
            SL["Button_Cancel"] ?? "Cancel");

        if (!confirm) return false;

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;

        Log.Info($"[PlaylistEdit] Unlinked from YouTube: {playlist.Id}");

        await _notifications.ShowToastAsync(
            titleKey: "EditPlaylist_Unlinked",
            messageKey: "EditPlaylist_Unlinked",
            severity: NotificationSeverity.Info,
            durationMs: 2500);

        return true;
    }
}