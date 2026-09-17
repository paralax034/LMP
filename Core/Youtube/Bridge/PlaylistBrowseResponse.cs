using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос страницы просмотра плейлиста (Browse).
/// Чистая легковесная DTO-модель: извлекает данные при разборе и не удерживает <see cref="JsonDocument"/> в памяти.
/// </summary>
internal partial class PlaylistBrowseResponse : IPlaylistData
{
    /// <inheritdoc />
    public bool IsAvailable { get; init; }

    /// <inheritdoc />
    public string? Title { get; init; }

    /// <inheritdoc />
    public string? Author { get; init; }

    /// <inheritdoc />
    public string? ChannelId { get; init; }

    /// <inheritdoc />
    public string? Description { get; init; }

    /// <inheritdoc />
    public int? Count { get; init; }

    /// <summary>
    /// Количество просмотров плейлиста.
    /// Поддерживает новый UI-отрендеренный объект заголовка (frameworkUpdates) и классический сайдбар.
    /// </summary>
    public long? ViewCount { get; init; }

    /// <summary>
    /// Дата создания или последнего обновления плейлиста (в строковом представлении).
    /// Извлекает чистое значение даты без локализованных префиксов («Обновлен», «Updated»).
    /// Поддерживает новый UI заголовка (frameworkUpdates) и парсинг третьего элемента
    /// статистики классического сайдбара.
    /// </summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>
    /// Вычисленный уровень доступа к плейлисту.
    /// Для owned playlists берётся из privacy dropdown, для остальных остаётся Unknown.
    /// </summary>
    public PlaylistVisibility Visibility { get; init; } = PlaylistVisibility.Unknown;

    /// <inheritdoc />
    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

    /// <summary>
    /// Список видео в текущей партии плейлиста.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos { get; init; } = [];

    /// <summary>
    /// Токен пагинации для получения следующей страницы списка видео.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// Данные о сессии пользователя для отслеживания контекста.
    /// </summary>
    public string? VisitorData { get; init; }

    /// <summary>
    /// Инициализирует DTO-модель, извлекая все свойства из корневого элемента JSON за один проход.
    /// </summary>
    /// <param name="content">Корневой JSON-элемент ответа Browse.</param>
    public PlaylistBrowseResponse(JsonElement content)
    {
        var sidebar = content
            .GetPropertyOrNull("sidebar")
            ?.GetPropertyOrNull("playlistSidebarRenderer")
            ?.GetPropertyOrNull("items");

        var sidebarPrimary = sidebar
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(0)
            ?.GetPropertyOrNull("playlistSidebarPrimaryInfoRenderer");

        var sidebarSecondary = sidebar
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(1)
            ?.GetPropertyOrNull("playlistSidebarSecondaryInfoRenderer");

        /// <summary>
        /// Renderer формы приватности плейлиста. Доступен, как правило, для плейлистов,
        /// принадлежащих текущему аутентифицированному пользователю.
        /// </summary>
        var privacyDropdownRenderer = sidebarPrimary
            ?.GetPropertyOrNull("privacyForm")
            ?.GetPropertyOrNull("dropdownFormFieldRenderer");

        var effectivePlaylistContents = ResolvePlaylistContents(content);

        IsAvailable = sidebar is not null || effectivePlaylistContents is not null;

        if (TryParseVisibility(privacyDropdownRenderer, out var visibility))
            Visibility = visibility;

        Title =
            sidebarPrimary
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(
                sidebarPrimary?.GetPropertyOrNull("title")?.GetPropertyOrNull("runs"))
            ?? sidebarPrimary
                ?.GetPropertyOrNull("titleForm")
                ?.GetPropertyOrNull("inlineFormRenderer")
                ?.GetPropertyOrNull("formField")
                ?.GetPropertyOrNull("textInputFormFieldRenderer")
                ?.GetPropertyOrNull("value")
                ?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("header")
                ?.GetPropertyOrNull("playlistHeaderRenderer")
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull();

        var authorDetails =
            sidebarSecondary?.GetPropertyOrNull("videoOwner")?.GetPropertyOrNull("videoOwnerRenderer");

        Author =
            authorDetails
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(
                authorDetails?.GetPropertyOrNull("title")?.GetPropertyOrNull("runs"))
            ?? content
                .GetPropertyOrNull("header")
                ?.GetPropertyOrNull("playlistHeaderRenderer")
                ?.GetPropertyOrNull("ownerText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(0)
                ?.GetPropertyOrNull("text")
                ?.GetStringOrNull();

        ChannelId =
            authorDetails
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull();

        Description =
            sidebarPrimary
                ?.GetPropertyOrNull("description")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(
                sidebarPrimary?.GetPropertyOrNull("description")?.GetPropertyOrNull("runs"))
            ?? sidebarPrimary
                ?.GetPropertyOrNull("descriptionForm")
                ?.GetPropertyOrNull("inlineFormRenderer")
                ?.GetPropertyOrNull("formField")
                ?.GetPropertyOrNull("textInputFormFieldRenderer")
                ?.GetPropertyOrNull("value")
                ?.GetStringOrNull();

        var stats = sidebarPrimary?.GetPropertyOrNull("stats");
        if (stats?.ValueKind == JsonValueKind.Array && stats.Value.GetArrayLength() > 0)
        {
            var text = GetTextFromStat(stats.Value[0]);
            if (text != null)
            {
                var val = YoutubeParsingHelpers.ParseLongFromText(text);
                if (val.HasValue)
                    Count = (int)val.Value;
            }
        }

        ViewCount = ExtractViewCount(content, sidebarPrimary);
        ReleaseDate = ExtractReleaseDate(content, sidebarPrimary);

        var thumbsElement = sidebarPrimary
            ?.GetPropertyOrNull("thumbnailRenderer")
            ?.GetPropertyOrNull("playlistVideoThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")
            ?? sidebarPrimary
            ?.GetPropertyOrNull("thumbnailRenderer")
            ?.GetPropertyOrNull("playlistCustomThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails");

        if (thumbsElement is { ValueKind: JsonValueKind.Array } thumbsArray)
        {
            int len = thumbsArray.GetArrayLength();
            if (len > 0)
            {
                var result = new ThumbnailData[len];
                for (int i = 0; i < len; i++)
                    result[i] = new ThumbnailData(thumbsArray[i]);
                Thumbnails = result;
            }
        }

        Videos = ExtractVideos(effectivePlaylistContents);
        ContinuationToken = ExtractContinuationToken(content, effectivePlaylistContents);
        VisitorData = content.GetVisitorData();
    }

    /// <summary>
    /// Выполняет структурный поиск <c>playlistVideoListRenderer</c> по всем вкладкам,
    /// секциям и элементам browse-ответа.
    /// </summary>
    private static JsonElement? ResolvePlaylistContents(JsonElement content)
    {
        var tabs = content
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs is { } tabsEl && tabsEl.ValueKind == JsonValueKind.Array)
        {
            int tabCount = tabsEl.GetArrayLength();
            for (int t = 0; t < tabCount; t++)
            {
                var tabContent = tabsEl[t]
                    .GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content");
                if (tabContent is null) continue;

                var found = FindRendererInTabContent(tabContent.Value);
                if (found is not null) return found;
            }
        }

        return content.FindFirstDescendantProperty("playlistVideoListRenderer");
    }

    /// <summary>
    /// Ищет <c>playlistVideoListRenderer</c> внутри содержимого одной вкладки.
    /// </summary>
    private static JsonElement? FindRendererInTabContent(JsonElement tabContent)
    {
        var sections = tabContent
            .GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sections is { } arr && arr.ValueKind == JsonValueKind.Array)
        {
            int sectionCount = arr.GetArrayLength();
            for (int s = 0; s < sectionCount; s++)
            {
                var section = arr[s];

                var itemContents = section
                    .GetPropertyOrNull("itemSectionRenderer")
                    ?.GetPropertyOrNull("contents");

                if (itemContents is { } items && items.ValueKind == JsonValueKind.Array)
                {
                    int itemCount = items.GetArrayLength();
                    for (int i = 0; i < itemCount; i++)
                    {
                        var renderer = items[i].GetPropertyOrNull("playlistVideoListRenderer");
                        if (renderer is not null) return renderer;
                    }
                }

                var direct = section.GetPropertyOrNull("playlistVideoListRenderer");
                if (direct is not null) return direct;
            }
        }

        var directInTab = tabContent.GetPropertyOrNull("playlistVideoListRenderer");
        if (directInTab is not null) return directInTab;

        return tabContent.FindFirstDescendantProperty("playlistVideoListRenderer");
    }

    /// <summary>
    /// Пытается определить visibility плейлиста из privacy dropdown.
    /// </summary>
    private static bool TryParseVisibility(JsonElement? root, out PlaylistVisibility visibility)
    {
        visibility = PlaylistVisibility.Unknown;

        if (root is null)
            return false;

        if (TryFindSelectedPrivacyEntry(root.Value, out var selectedEntry) &&
            TryParseVisibilityFromEntry(selectedEntry, out visibility))
        {
            return true;
        }

        return TryParseVisibilityFromEntry(root.Value, out visibility);
    }

    /// <summary>
    /// Рекурсивно ищет объект-элемент dropdown, у которого <c>isSelected = true</c>.
    /// </summary>
    private static bool TryFindSelectedPrivacyEntry(JsonElement element, out JsonElement selectedEntry)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var isSelected = element.GetPropertyOrNull("isSelected")?.GetBooleanOrNull();
                    if (isSelected == true)
                    {
                        selectedEntry = element;
                        return true;
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        if (TryFindSelectedPrivacyEntry(property.Value, out selectedEntry))
                            return true;
                    }

                    break;
                }

            case JsonValueKind.Array:
                {
                    int len = element.GetArrayLength();
                    for (int i = 0; i < len; i++)
                    {
                        if (TryFindSelectedPrivacyEntry(element[i], out selectedEntry))
                            return true;
                    }

                    break;
                }
        }

        selectedEntry = default;
        return false;
    }

    /// <summary>
    /// Пытается извлечь visibility из конкретного элемента dropdown.
    /// </summary>
    private static bool TryParseVisibilityFromEntry(JsonElement element, out PlaylistVisibility visibility)
    {
        visibility = PlaylistVisibility.Unknown;

        var privacyCode = element.FindFirstDescendantProperty("playlistPrivacy")?.GetStringOrNull();
        if (TryMapPrivacyCode(privacyCode, out visibility))
            return true;

        var intValue = element.FindFirstDescendantProperty("int32Value")?.GetInt32OrNull();
        if (TryMapPrivacyInt(intValue, out visibility))
            return true;

        var iconType = element.FindFirstDescendantProperty("iconType")?.GetStringOrNull();
        if (TryMapPrivacyIcon(iconType, out visibility))
            return true;

        return false;
    }

    private static bool TryMapPrivacyCode(string? code, out PlaylistVisibility visibility)
    {
        visibility = code switch
        {
            "PRIVATE" => PlaylistVisibility.Private,
            "UNLISTED" => PlaylistVisibility.Unlisted,
            "PUBLIC" => PlaylistVisibility.Public,
            _ => PlaylistVisibility.Unknown
        };

        return visibility != PlaylistVisibility.Unknown;
    }

    private static bool TryMapPrivacyInt(int? value, out PlaylistVisibility visibility)
    {
        visibility = value switch
        {
            0 => PlaylistVisibility.Private,
            2 => PlaylistVisibility.Unlisted,
            1 => PlaylistVisibility.Public,
            _ => PlaylistVisibility.Unknown
        };

        return visibility != PlaylistVisibility.Unknown;
    }

    private static bool TryMapPrivacyIcon(string? iconType, out PlaylistVisibility visibility)
    {
        visibility = iconType switch
        {
            "PRIVACY_PRIVATE" => PlaylistVisibility.Private,
            "PRIVACY_UNLISTED" => PlaylistVisibility.Unlisted,
            "PRIVACY_PUBLIC" => PlaylistVisibility.Public,
            _ => PlaylistVisibility.Unknown
        };

        return visibility != PlaylistVisibility.Unknown;
    }

    private static long? ExtractViewCount(JsonElement content, JsonElement? sidebarPrimary)
    {
        // 1. Попытка через новый UI (frameworkUpdates)
        var mutations = content
            .GetPropertyOrNull("frameworkUpdates")
            ?.GetPropertyOrNull("entityBatchUpdate")
            ?.GetPropertyOrNull("mutations");

        if (mutations?.ValueKind == JsonValueKind.Array)
        {
            foreach (var mutation in mutations.Value.EnumerateArray())
            {
                var metadataRows = mutation
                    .GetPropertyOrNull("payload")
                    ?.GetPropertyOrNull("pageHeaderEntity")
                    ?.GetPropertyOrNull("header")
                    ?.GetPropertyOrNull("pageHeaderViewModel")
                    ?.GetPropertyOrNull("metadata")
                    ?.GetPropertyOrNull("contentMetadataViewModel")
                    ?.GetPropertyOrNull("metadataRows");

                if (metadataRows?.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in metadataRows.Value.EnumerateArray())
                    {
                        var parts = row.GetPropertyOrNull("metadataParts");
                        if (parts?.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in parts.Value.EnumerateArray())
                            {
                                var text = part.GetPropertyOrNull("text")?.GetPropertyOrNull("content")?.GetStringOrNull();
                                if (text != null && (text.Contains("view", StringComparison.OrdinalIgnoreCase) ||
                                                     text.Contains("просмотр", StringComparison.OrdinalIgnoreCase)))
                                {
                                    var views = YoutubeParsingHelpers.ParseLongFromText(text);
                                    if (views.HasValue) return views;
                                }
                            }
                        }
                    }
                }
            }
        }

        // 2. Попытка через классический Sidebar stats[1]
        var stats = sidebarPrimary?.GetPropertyOrNull("stats");
        if (stats?.ValueKind == JsonValueKind.Array)
        {
            int len = stats.Value.GetArrayLength();
            if (len > 1)
            {
                var text = GetTextFromStat(stats.Value[1]);
                var views = YoutubeParsingHelpers.ParseLongFromText(text);
                if (views.HasValue) return views;
            }
        }

        return null;
    }

    private static DateOnly? ExtractReleaseDate(JsonElement content, JsonElement? sidebarPrimary)
    {
        // 1. Попытка через новый UI (frameworkUpdates)
        var mutations = content
            .GetPropertyOrNull("frameworkUpdates")
            ?.GetPropertyOrNull("entityBatchUpdate")
            ?.GetPropertyOrNull("mutations");

        if (mutations?.ValueKind == JsonValueKind.Array)
        {
            foreach (var mutation in mutations.Value.EnumerateArray())
            {
                var metadataRows = mutation
                    .GetPropertyOrNull("payload")
                    ?.GetPropertyOrNull("pageHeaderEntity")
                    ?.GetPropertyOrNull("header")
                    ?.GetPropertyOrNull("pageHeaderViewModel")
                    ?.GetPropertyOrNull("metadata")
                    ?.GetPropertyOrNull("contentMetadataViewModel")
                    ?.GetPropertyOrNull("metadataRows");

                if (metadataRows?.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in metadataRows.Value.EnumerateArray())
                    {
                        var parts = row.GetPropertyOrNull("metadataParts");
                        if (parts?.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in parts.Value.EnumerateArray())
                            {
                                var text = part.GetPropertyOrNull("text")
                                    ?.GetPropertyOrNull("content")
                                    ?.GetStringOrNull();

                                if (text != null &&
                                    (YoutubeParsingHelpers.ParseYearFromText(text).HasValue || YoutubeParsingHelpers.IsRelativeDate(text)))
                                {
                                    return TryParseDate(text);
                                }
                            }
                        }
                    }
                }
            }
        }

        // 2. Попытка через классический Sidebar stats[2] (третий элемент массива)
        var stats = sidebarPrimary?.GetPropertyOrNull("stats");
        if (stats?.ValueKind == JsonValueKind.Array)
        {
            int len = stats.Value.GetArrayLength();
            if (len > 2)
            {
                var dateStat = stats.Value[2];

                var dateFromRuns = GetDateFromStatRuns(dateStat.GetPropertyOrNull("runs"));
                if (dateFromRuns != null)
                    return TryParseDate(dateFromRuns);

                var simpleText = dateStat.GetPropertyOrNull("simpleText")?.GetStringOrNull();
                if (simpleText != null)
                    return TryParseDate(simpleText);
            }
        }

        return null;
    }

    /// <summary>
    /// Пытается преобразовать локализованную строку YouTube в <see cref="DateOnly"/>.
    /// Поддерживает строгие форматы («20 нояб. 2025 г.», «Jan 13, 2026»),
    /// относительные даты («5 дней назад», «сегодня») и извлечение года.
    /// </summary>
    private static DateOnly? TryParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var clean = StripUpdatePrefix(text);
        var span = clean.AsSpan().Trim();
        if (span.IsEmpty) return null;

        if (DateOnly.TryParseExact(clean, YoutubeParsingHelpers.DateFormats,
                YoutubeParsingHelpers.RuCulture, DateTimeStyles.AllowWhiteSpaces, out var dateRu))
            return dateRu;
        if (DateOnly.TryParseExact(clean, YoutubeParsingHelpers.DateFormats,
                YoutubeParsingHelpers.EnCulture, DateTimeStyles.AllowWhiteSpaces, out var dateEn))
            return dateEn;

        if (DateOnly.TryParse(clean, YoutubeParsingHelpers.RuCulture,
                DateTimeStyles.AllowWhiteSpaces, out var flexRu)) return flexRu;
        if (DateOnly.TryParse(clean, YoutubeParsingHelpers.EnCulture,
                DateTimeStyles.AllowWhiteSpaces, out var flexEn)) return flexEn;
        if (DateOnly.TryParse(clean, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var flexInv)) return flexInv;

        if (YoutubeParsingHelpers.IsRelativeDate(clean))
        {
            var now = DateTime.UtcNow;

            if (span.Contains("сегодня", StringComparison.OrdinalIgnoreCase) ||
                span.Contains("today", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(now);

            if (span.Contains("вчера", StringComparison.OrdinalIgnoreCase) ||
                span.Contains("yesterday", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(now.AddDays(-1));

            var val = YoutubeParsingHelpers.ParseLongFromText(clean);
            if (val.HasValue)
            {
                int delta = (int)val.Value;

                if (span.Contains("недел", StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("week", StringComparison.OrdinalIgnoreCase))
                    return DateOnly.FromDateTime(now.AddDays(-delta * 7));

                if (span.Contains("месяц", StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("month", StringComparison.OrdinalIgnoreCase))
                    return DateOnly.FromDateTime(now.AddMonths(-delta));

                if (span.Contains("час", StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("минут", StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("minute", StringComparison.OrdinalIgnoreCase))
                    return DateOnly.FromDateTime(now);

                return DateOnly.FromDateTime(now.AddDays(-delta));
            }
        }

        var year = YoutubeParsingHelpers.ParseYearFromText(clean);
        return year.HasValue ? new DateOnly(year.Value, 1, 1) : null;
    }

    private static IReadOnlyList<PlaylistVideoData> ExtractVideos(JsonElement? effectivePlaylistContents)
    {
        var contents = effectivePlaylistContents?.GetPropertyOrNull("contents");
        if (contents is null || contents.Value.ValueKind != JsonValueKind.Array)
            return [];

        var array = contents.Value;
        int len = array.GetArrayLength();

        // Предварительный проход без аллокаций для подсчета валидных элементов
        int validCount = 0;
        for (int i = 0; i < len; i++)
        {
            if (array[i].GetPropertyOrNull("playlistVideoRenderer") is not null)
                validCount++;
        }

        if (validCount == 0) return [];

        var result = new PlaylistVideoData[validCount];
        int index = 0;
        for (int i = 0; i < len; i++)
        {
            var videoRenderer = array[i].GetPropertyOrNull("playlistVideoRenderer");
            if (videoRenderer is not null)
            {
                result[index++] = new PlaylistVideoData(videoRenderer.Value);
            }
        }

        return result;
    }

    private static string? ExtractContinuationToken(JsonElement content, JsonElement? effectivePlaylistContents)
    {
        if (effectivePlaylistContents != null)
        {
            var continuations = effectivePlaylistContents.Value.GetPropertyOrNull("continuations");
            if (continuations != null)
            {
                var token = continuations.Value.EnumerateArrayOrNull()?.FirstOrNull()
                    ?.GetPropertyOrNull("nextContinuationData")
                    ?.GetPropertyOrNull("continuation")
                    ?.GetStringOrNull();
                if (token != null) return token;
            }

            var contents = effectivePlaylistContents.Value.GetPropertyOrNull("contents");
            if (contents != null)
            {
                var token = BridgeUtils.FindTokenInContents(contents.Value);
                if (token != null) return token;
            }
        }

        var actions = content.GetPropertyOrNull("onResponseReceivedActions");
        if (actions != null)
        {
            var continuationItems = actions.Value.EnumerateArrayOrNull()?.FirstOrNull()
                ?.GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems");

            if (continuationItems != null)
            {
                var token = BridgeUtils.FindTokenInContents(continuationItems.Value);
                if (token != null) return token;
            }
        }

        return null;
    }

    #region High-Performance JSON Helpers

    /// <summary>
    /// Извлекает чистое значение даты из массива <c>runs</c> элемента статистики плейлиста.
    /// Для массивов с несколькими элементами пропускает первый run (локализованный префикс
    /// вроде «Обновлен ») и склеивает оставшиеся через <c>stackalloc</c>.
    /// Для одиночного run удаляет известные префиксы через <see cref="StripUpdatePrefix"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? GetDateFromStatRuns(JsonElement? runsElement)
    {
        if (runsElement is null || runsElement.Value.ValueKind != JsonValueKind.Array)
            return null;

        var array = runsElement.Value;
        int len = array.GetArrayLength();
        if (len == 0) return null;

        // Одиночный run: префикс и дата в одной строке ("Обновлено сегодня")
        if (len == 1)
        {
            var text = array[0].GetPropertyOrNull("text")?.GetStringOrNull();
            return text != null ? StripUpdatePrefix(text) : null;
        }

        // Два run: первый — префикс, второй — чистая дата
        if (len == 2)
            return array[1].GetPropertyOrNull("text")?.GetStringOrNull()?.Trim();

        // 3+ runs: первый — префикс, остальные — составные части даты ("5" + " дней назад")
        int dateRunCount = len - 1;
        int totalLen = 0;
        var strings = new string?[dateRunCount];

        for (int i = 0; i < dateRunCount; i++)
        {
            var t = array[i + 1].GetPropertyOrNull("text")?.GetStringOrNull();
            if (t != null)
            {
                strings[i] = t;
                totalLen += t.Length;
            }
        }

        if (totalLen == 0) return null;

        Span<char> span = totalLen <= 128 ? stackalloc char[totalLen] : new char[totalLen];
        int pos = 0;

        for (int i = 0; i < dateRunCount; i++)
        {
            if (strings[i] is { } s)
            {
                s.AsSpan().CopyTo(span[pos..]);
                pos += s.Length;
            }
        }

        return new string(span[..pos].Trim());
    }

    /// <summary>
    /// Удаляет известные локализованные префиксы обновления из строки даты.
    /// Поддерживает русские («Обновлено», «Обновлен», «Обновлена»)
    /// и английские («Updated», «Last updated on») варианты.
    /// При отсутствии совпадений возвращает исходную ссылку без аллокаций.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string StripUpdatePrefix(string text)
    {
        var span = text.AsSpan();
        int prefixLen = 0;

        if (span.StartsWith("Обновлено ", StringComparison.OrdinalIgnoreCase))
            prefixLen = 10;
        else if (span.StartsWith("Обновлена ", StringComparison.OrdinalIgnoreCase))
            prefixLen = 10;
        else if (span.StartsWith("Обновлен ", StringComparison.OrdinalIgnoreCase))
            prefixLen = 9;
        else if (span.StartsWith("Last updated on ", StringComparison.OrdinalIgnoreCase))
            prefixLen = 16;
        else if (span.StartsWith("Updated ", StringComparison.OrdinalIgnoreCase))
            prefixLen = 8;

        if (prefixLen == 0)
            return text;

        var remaining = span[prefixLen..].Trim();
        return remaining.IsEmpty ? text : new string(remaining);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? GetTextFromStat(JsonElement stat) =>
        stat.GetPropertyOrNull("simpleText")?.GetStringOrNull()
        ?? YoutubeParsingHelpers.ConcatTextRuns(stat.GetPropertyOrNull("runs"));

    #endregion
}

internal partial class PlaylistBrowseResponse
{
    /// <summary>
    /// Парсит ответ Browse плейлиста из сырой JSON-строки.
    /// Освобождает <see cref="JsonDocument"/> сразу после формирования модели.
    /// </summary>
    public static PlaylistBrowseResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlaylistBrowseResponse(doc.RootElement);
    }

    /// <summary>
    /// Парсит ответ Browse плейлиста напрямую из сетевого потока без промежуточных строк в LOH.
    /// Освобождает <see cref="JsonDocument"/> сразу после формирования модели.
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр плоского ответа плейлиста.</returns>
    public static async ValueTask<PlaylistBrowseResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlaylistBrowseResponse(doc.RootElement);
    }
}