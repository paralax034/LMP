using System.Text.RegularExpressions;
using LMP.Core.Models;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Services;

/// <summary>
/// Сервис сопоставления треков с очисткой метаданных и контролем хронометража.
/// </summary>
public sealed partial class TrackMatcher
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    [GeneratedRegex(@"\.(?:mp3|flac|wav|m4a|aac)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtensionRegex();

    [GeneratedRegex(@"(?:www\.|https?:\/\/|[a-z0-9_\.-]+\.(?:com|ru|org|net|me|cc))\S*", RegexOptions.IgnoreCase)]
    private static partial Regex WebAdsRegex();

    [GeneratedRegex(@"\[(?:remastered|remaster|official\s+video|official\s+audio|audio|video|lyrics?|клип|hq|hd)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketGarbageRegex();

    [GeneratedRegex(@"\((?:official\s+video|official\s+audio|lyric\s+video|lyrics?|клип)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenGarbageRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.None)]
    private static partial Regex MultipleSpacesRegex();

    public TrackMatcher(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
    }

    /// <summary>
    /// Очищает название и автора от мусорных тегов, расширений и рекламы.
    /// </summary>
    public static (string CleanTitle, string CleanArtist) Sanitize(string title, string artist)
    {
        string t = title ?? string.Empty;
        string a = artist ?? string.Empty;

        t = FileExtensionRegex().Replace(t, string.Empty);
        a = FileExtensionRegex().Replace(a, string.Empty);

        t = WebAdsRegex().Replace(t, string.Empty);
        a = WebAdsRegex().Replace(a, string.Empty);

        t = BracketGarbageRegex().Replace(t, string.Empty);
        t = ParenGarbageRegex().Replace(t, string.Empty);

        if (string.IsNullOrWhiteSpace(a) || a.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            var dashIndex = t.IndexOfAny(['—', '-']);
            if (dashIndex > 0 && dashIndex < t.Length - 1)
            {
                a = t[..dashIndex].Trim();
                t = t[(dashIndex + 1)..].Trim();
            }
        }

        t = MultipleSpacesRegex().Replace(t.Trim(), " ");
        a = MultipleSpacesRegex().Replace(a.Trim(), " ");

        return (t, string.IsNullOrWhiteSpace(a) ? "Unknown Artist" : a);
    }

    /// <summary>
    /// Проверяет допустимость отклонения хронометража (не более 25 секунд).
    /// </summary>
    public static bool IsDurationAcceptable(TimeSpan target, TimeSpan candidate)
    {
        if (target <= TimeSpan.Zero || candidate <= TimeSpan.Zero)
        {
            return true;
        }

        return Math.Abs((target - candidate).TotalSeconds) <= 25.0;
    }

   /// <summary>
    /// Выполняет сопоставление трека: L1 SQLite -> YouTube Music -> YouTube Video.
    /// </summary>
    public async Task<TrackInfo?> MatchAsync(ExternalTrack track, bool isRelaxed = false, CancellationToken ct = default)
    {
        var (cleanTitle, cleanArtist) = Sanitize(track.Title, track.Artist);
        string searchQuery = $"{cleanArtist} - {cleanTitle}";
        double maxDeltaSec = isRelaxed ? 45.0 : 25.0;

        // 1. Поиск по локальной базе SQLite (L1) по чистому названию
        try
        {
            var localMatches = await _library.SearchTracksAsync(cleanTitle, limit: 3, ct: ct).ConfigureAwait(false);
            for (int i = 0; i < localMatches.Count; i++)
            {
                var candidate = localMatches[i];
                if ((track.Duration <= TimeSpan.Zero || Math.Abs((track.Duration - candidate.Duration).TotalSeconds) <= maxDeltaSec) &&
                    (candidate.Title.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                     cleanTitle.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[TrackMatcher] Local-First miss: {ex.Message}");
        }

        // 2. Сетевой поиск в YouTube Music (MusicSong)
        try
        {
            var results = await _youtube.SearchFastAsync(
                searchQuery,
                maxResults: isRelaxed ? 3 : 1,
                filter: SearchFilter.MusicSong,
                ct: ct).ConfigureAwait(false);

            for (int i = 0; i < results.Count; i++)
            {
                if (track.Duration <= TimeSpan.Zero || Math.Abs((track.Duration - results[i].Duration).TotalSeconds) <= maxDeltaSec)
                {
                    return results[i];
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"[TrackMatcher] MusicSong search failed for '{searchQuery}': {ex.Message}");
        }

        // 3. Fallback: Обычный видео-поиск
        try
        {
            var fallback = await _youtube.SearchFastAsync(
                searchQuery,
                maxResults: isRelaxed ? 3 : 1,
                filter: SearchFilter.Video,
                ct: ct).ConfigureAwait(false);

            for (int i = 0; i < fallback.Count; i++)
            {
                if (track.Duration <= TimeSpan.Zero || Math.Abs((track.Duration - fallback[i].Duration).TotalSeconds) <= maxDeltaSec)
                {
                    return fallback[i];
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"[TrackMatcher] Fallback search failed for '{searchQuery}': {ex.Message}");
        }

        return null;
    }
}
