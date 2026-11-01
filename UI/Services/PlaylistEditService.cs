using LMP.UI.Dialogs;

namespace LMP.UI.Services;

/// <summary>
/// UI-сервис редактирования плейлистов: показ диалогов и координация UI-потока.
/// Доменная логика (клонирование, привязка, отвязка) строго делегирована в <see cref="PlaylistService"/>.
/// </summary>
public sealed class PlaylistEditService
{
    private readonly PlaylistService _playlistService;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly NotificationService _notifications;

    private static LocalizationService SL => LocalizationService.Instance;

    public PlaylistEditService(
        PlaylistService playlistService,
        CookieAuthService auth,
        NotificationService notifications,
        DialogService dialog)
    {
        _playlistService = playlistService;
        _auth = auth;
        _notifications = notifications;
        _dialog = dialog;
    }

    public sealed record EditResult(bool Changed, Playlist Playlist);

    /// <summary>
    /// Координирует процесс редактирования метаданных и облачной привязки плейлиста.
    /// </summary>
    /// <param name="playlistId">Идентификатор редактируемого плейлиста.</param>
    /// <param name="lockNavigation">Делегат блокировки UI-навигации при длительных сетевых операциях.</param>
    /// <param name="unlockNavigation">Делегат снятия блокировки UI-навигации.</param>
    /// <returns>Результат редактирования с актуальной моделью плейлиста либо <c>null</c> при отмене.</returns>
    public async Task<EditResult?> EditPlaylistAsync(
        string playlistId,
        Action<string> lockNavigation,
        Action unlockNavigation)
    {
        var playlist = await _playlistService.GetPlaylistAsync(playlistId).ConfigureAwait(false);
        if (playlist == null) return null;

        if (!playlist.IsEditable)
        {
            var message = !string.IsNullOrEmpty(playlist.Author)
                ? string.Format(SL["Playlist_ReadOnly_ByAuthor"], playlist.Author)
                : SL["Playlist_ReadOnly"];

            await _dialog.ShowInfoAsync(SL["Dialog_Warning_Title"], message);
            return null;
        }

        var tracks = await _playlistService.GetPlaylistTracksAsync(playlistId).ConfigureAwait(false);
        var result = await _dialog.ShowEditPlaylistDialogAsync(playlist, tracks);
        if (result == null) return null;

        if (result.ShouldCreateCopy)
            return await CreateCopyAsync(playlist, result, lockNavigation, unlockNavigation).ConfigureAwait(false);

        bool changed = false;
        bool nameChanged = false;
        bool descChanged = false;
        bool thumbChanged = false;
        string baselineName = playlist.Name;
        string? baselineDesc = playlist.Description;

        // 1. Rename
        if (!LibraryService.IsSystemPlaylist(playlistId))
        {
            var newName = result.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(newName) &&
                !string.Equals(newName, playlist.Name, StringComparison.Ordinal))
            {
                playlist.Name = newName;
                changed = true;
                nameChanged = true;
            }
        }

        // 2. Thumbnail
        if (!string.Equals(result.ThumbnailUrl, playlist.ThumbnailUrl, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                if (PlaylistEditorViewModel.IsValidUri(result.ThumbnailUrl))
                {
                    playlist.ThumbnailUrl = result.ThumbnailUrl;
                    playlist.ComputedColor = null;
                    changed = true;
                    thumbChanged = true;
                }
                else
                {
                    Log.Warn($"[PlaylistEdit] Invalid thumbnail path: {result.ThumbnailUrl}");

                    await _notifications.ShowToastAsync(
                        titleKey: "Dialog_Warning_Title",
                        messageKey: "Error_InvalidThumbnailUrl",
                        severity: NotificationSeverity.Warning);
                }
            }
            else
            {
                playlist.ThumbnailUrl = null;
                playlist.ComputedColor = null;
                changed = true;
                thumbChanged = true;
            }
        }

        // 3. Description
        if (!string.Equals(result.Description?.Trim(), playlist.Description?.Trim(), StringComparison.Ordinal))
        {
            playlist.Description = result.Description?.Trim();
            changed = true;
            descChanged = true;
        }

        // 4. Custom Color
        if (!string.Equals(result.CustomColor, playlist.CustomColor, StringComparison.Ordinal))
        {
            playlist.CustomColor = result.CustomColor;
            changed = true;
        }

        // 5. Computed Color
        if (result.ComputedColor != null &&
            !string.Equals(result.ComputedColor, playlist.ComputedColor, StringComparison.OrdinalIgnoreCase))
        {
            playlist.ComputedColor = result.ComputedColor;
            changed = true;
        }

        // Сохраняем отредактированные метаданные до изменения статуса привязки
        if (changed)
        {
            playlist.UpdatedAt = DateTime.Now;
            await _playlistService.AddOrUpdatePlaylistAsync(playlist).ConfigureAwait(false);

            bool staysSynced = playlist.IsCloudBound &&
                               (!result.SyncToCloud.HasValue || result.SyncToCloud.Value);
            if (staysSynced && (nameChanged || descChanged || thumbChanged))
            {
                _playlistService.EnqueueCloudMetadataUpdate(
                    playlist,
                    newName: nameChanged ? playlist.Name : null,
                    baselineName: baselineName,
                    newDescription: descChanged ? (playlist.Description ?? string.Empty) : null,
                    baselineDescription: baselineDesc,
                    newThumbnailUrl: playlist.ThumbnailUrl,
                    thumbnailChanged: thumbChanged);
            }
        }

        // 6. Sync toggle: выполняется строго после фиксации метаданных, чтобы не затереть облачные идентификаторы
        if (result.SyncToCloud.HasValue && result.SyncToCloud.Value != playlist.IsFromAccount)
        {
            bool wantsSync = result.SyncToCloud.Value;

            if (wantsSync && !playlist.IsFromAccount && _auth.IsAuthenticated)
            {
                bool linked = await TryLinkToCloudAsync(playlistId, lockNavigation, unlockNavigation).ConfigureAwait(false);
                if (linked)
                {
                    var fresh = await _playlistService.GetPlaylistAsync(playlistId).ConfigureAwait(false);
                    if (fresh != null)
                    {
                        playlist.YoutubeId = fresh.YoutubeId;
                        playlist.SyncMode = fresh.SyncMode;
                    }
                    changed = true;
                }
            }
            else if (!wantsSync && playlist.IsFromAccount)
            {
                bool unlinked = await TryUnlinkFromCloudAsync(playlist).ConfigureAwait(false);
                if (unlinked)
                {
                    playlist.YoutubeId = null;
                    playlist.SyncMode = PlaylistSyncMode.LocalOnly;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            await _notifications.ShowToastAsync(
                titleKey: "EditPlaylist_Saved",
                messageKey: "EditPlaylist_Saved",
                severity: NotificationSeverity.Success,
                durationMs: 2000);

            NotificationService.PlaySuccessSound();
        }

        return new EditResult(changed, playlist);
    }

    private async Task<EditResult?> CreateCopyAsync(
        Playlist original,
        EditPlaylistResult editorResult,
        Action<string> lockNavigation,
        Action unlockNavigation)
    {
        _ = lockNavigation;
        _ = unlockNavigation;
        try
        {
            var copyName = string.IsNullOrWhiteSpace(editorResult.Name)
                ? original.Name
                : editorResult.Name.Trim();

            if (string.Equals(copyName, original.Name, StringComparison.Ordinal))
                copyName = $"{copyName} ({SL["Playlist_CopySuffix"]})";

            var copy = await _playlistService.CreateCopyAsync(
                original.Id,
                copyName,
                editorResult.Description,
                editorResult.CustomColor,
                editorResult.ComputedColor,
                editorResult.ThumbnailUrl).ConfigureAwait(false);

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
                severity: NotificationSeverity.Error);

            _notifications.TryPlayErrorSound();
            return null;
        }
    }

    private async Task<bool> TryLinkToCloudAsync(
        string localPlaylistId,
        Action<string> lockNavigation,
        Action unlockNavigation)
    {
        _ = lockNavigation;
        _ = unlockNavigation;
        try
        {
            bool success = await _playlistService.LinkToCloudAsync(localPlaylistId).ConfigureAwait(false);
            if (!success)
            {
                await _notifications.ShowToastAsync(
                    titleKey: "Dialog_Error_Title",
                    messageKey: "Playlist_CloudCreateFailed",
                    severity: NotificationSeverity.Error);

                _notifications.TryPlayErrorSound();
                return false;
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
    }

    private async Task<bool> TryUnlinkFromCloudAsync(Playlist playlist)
    {
        var confirm = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"],
            SL["Playlist_UnlinkConfirm"] +
                   "The playlist will remain in your YouTube account, " +
                   "but local changes will no longer sync.",
            SL["Playlist_Unlink"],
            SL["Button_Cancel"]);

        if (!confirm) return false;

        await _playlistService.UnlinkFromCloudAsync(playlist.Id).ConfigureAwait(false);

        await _notifications.ShowToastAsync(
            titleKey: "EditPlaylist_Unlinked",
            messageKey: "EditPlaylist_Unlinked",
            severity: NotificationSeverity.Info,
            durationMs: 2500);

        return true;
    }
}