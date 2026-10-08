using System.Text.RegularExpressions;
using LMP.Core.Models;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Services;

/// <summary>
/// Высокоточный каскадный сервис сопоставления треков с расширенной выборкой кандидатов.
/// </summary>
public sealed partial class TrackMatcher
{
    private const double StrictDurationDeltaSec = 25.0;
    private const double RelaxedDurationDeltaSec = 45.0;
    private const double MinimumConfidenceThreshold = 0.50;

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    [GeneratedRegex(
        @"\.(?:mp3|flac|wav|m4a|aac)$|" +
        @"(?:https?:\/\/|www\.)\S+|" +
        @"\[[^\]]*(?:remaster|video|audio|lyrics?|клип)[^\]]*\]|" +
        @"\([^\)]*(?:official|video|audio|lyric|lyrics?|клип)[^\)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnifiedSanitizerRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.None)]
    private static partial Regex MultipleSpacesRegex();

    public TrackMatcher(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
    }

    public static (string CleanTitle, string CleanArtist) Sanitize(string title, string artist)
    {
        string t = title ?? string.Empty;
        string a = artist ?? string.Empty;

        t = UnifiedSanitizerRegex().Replace(t, string.Empty);
        a = UnifiedSanitizerRegex().Replace(a, string.Empty);

        if (string.IsNullOrWhiteSpace(a) || a.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            int dashIndex = t.IndexOfAny(['—', '-']);
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

    public static string GetPrimaryArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;

        int separatorIdx = artist.IndexOfAny([',', ';', '&', '/']);
        if (separatorIdx > 0)
        {
            return artist[..separatorIdx].Trim();
        }

        int featIdx = artist.IndexOf(" feat", StringComparison.OrdinalIgnoreCase);
        if (featIdx > 0) return artist[..featIdx].Trim();

        int ftIdx = artist.IndexOf(" ft", StringComparison.OrdinalIgnoreCase);
        if (ftIdx > 0) return artist[..ftIdx].Trim();

        return artist.Trim();
    }

    public static bool IsDurationAcceptable(TimeSpan target, TimeSpan candidate, double maxDeltaSec = StrictDurationDeltaSec)
    {
        if (target <= TimeSpan.Zero || candidate <= TimeSpan.Zero)
        {
            return true;
        }

        return Math.Abs((target - candidate).TotalSeconds) <= maxDeltaSec;
    }

    public async Task<TrackInfo?> MatchAsync(ExternalTrack track, bool isRelaxed = false, CancellationToken ct = default)
    {
        var (cleanTitle, cleanArtist) = Sanitize(track.Title, track.Artist);
        string primaryArtist = GetPrimaryArtist(cleanArtist);
        double maxDelta = isRelaxed ? RelaxedDurationDeltaSec : StrictDurationDeltaSec;

        var targetTitleTokens = Tokenize(cleanTitle);
        var targetArtistTokens = Tokenize(primaryArtist);

        // ═══════════════════════════════════════════════════════════════════
        // ЭТАП 1: Local-First (L1 SQLite) — выборка 5 кандидатов
        // ═══════════════════════════════════════════════════════════════════
        try
        {
            var localCandidates = await _library.SearchTracksAsync(cleanTitle, limit: 5, ct: ct).ConfigureAwait(false);
            var bestLocal = SelectBestCandidate(localCandidates, targetTitleTokens, targetArtistTokens, track.Duration, maxDelta);
            if (bestLocal is { Score: >= 0.70 })
            {
                Log.Debug($"[TrackMatcher] Local L1 match hit: '{cleanArtist} - {cleanTitle}' -> '{bestLocal.Track.Title}'");
                return bestLocal.Track;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[TrackMatcher] Local search failed: {ex.Message}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // ЭТАП 2: YouTube Music (MusicSong) — расширенная выборка до 8-10 кандидатов
        // ═══════════════════════════════════════════════════════════════════
        string primaryQuery = !string.IsNullOrWhiteSpace(primaryArtist) && !primaryArtist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)
            ? $"{primaryArtist} {cleanTitle}"
            : cleanTitle;

        int musicCandidatesCount = isRelaxed ? 10 : 8;

        try
        {
            var candidates = await _youtube.SearchFastAsync(
                primaryQuery,
                maxResults: musicCandidatesCount,
                filter: SearchFilter.MusicSong,
                ct: ct).ConfigureAwait(false);

            var best = SelectBestCandidate(candidates, targetTitleTokens, targetArtistTokens, track.Duration, maxDelta);
            if (best is { Score: >= MinimumConfidenceThreshold })
            {
                return best.Track;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"[TrackMatcher] Primary MusicSong search failed for '{primaryQuery}': {ex.Message}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // ЭТАП 3: YouTube Music (MusicSong) — запрос по полному списку авторов
        // ═══════════════════════════════════════════════════════════════════
        if (!string.Equals(primaryArtist, cleanArtist, StringComparison.OrdinalIgnoreCase))
        {
            string fullQuery = $"{cleanArtist} {cleanTitle}";
            try
            {
                var candidates = await _youtube.SearchFastAsync(
                    fullQuery,
                    maxResults: 6,
                    filter: SearchFilter.MusicSong,
                    ct: ct).ConfigureAwait(false);

                var best = SelectBestCandidate(candidates, targetTitleTokens, targetArtistTokens, track.Duration, maxDelta);
                if (best is { Score: >= MinimumConfidenceThreshold })
                {
                    return best.Track;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"[TrackMatcher] Full MusicSong search failed for '{fullQuery}': {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // ЭТАП 4: YouTube Video Search (Fallback — выборка до 8-10 кандидатов)
        // ═══════════════════════════════════════════════════════════════════
        try
        {
            var fallbackCandidates = await _youtube.SearchFastAsync(
                primaryQuery,
                maxResults: musicCandidatesCount,
                filter: SearchFilter.Video,
                ct: ct).ConfigureAwait(false);

            var best = SelectBestCandidate(fallbackCandidates, targetTitleTokens, targetArtistTokens, track.Duration, maxDelta + 10.0);
            if (best is { Score: >= (MinimumConfidenceThreshold - 0.05) })
            {
                Log.Debug($"[TrackMatcher] Fallback Video matched: '{cleanArtist} - {cleanTitle}' -> '{best.Track.Title}' (score: {best.Score:F2})");
                return best.Track;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"[TrackMatcher] Video fallback search failed: {ex.Message}");
        }

        return null;
    }

    #region Intelligent Scoring Engine

    private sealed record CandidateMatch(TrackInfo Track, double Score);

    private static CandidateMatch? SelectBestCandidate(
        List<TrackInfo> candidates,
        HashSet<string> targetTitleTokens,
        HashSet<string> targetArtistTokens,
        TimeSpan targetDuration,
        double maxDeltaSec)
    {
        if (candidates.Count == 0) return null;

        CandidateMatch? best = null;

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            double score = CalculateConfidenceScore(candidate, targetTitleTokens, targetArtistTokens, targetDuration, maxDeltaSec);

            if (score > 0 && (best == null || score > best.Score))
            {
                best = new CandidateMatch(candidate, score);
            }
        }

        return best;
    }

    private static double CalculateConfidenceScore(
        TrackInfo candidate,
        HashSet<string> targetTitleTokens,
        HashSet<string> targetArtistTokens,
        TimeSpan targetDuration,
        double maxDeltaSec)
    {
        // 1. Проверка длительности
        double durationScore = 1.0;
        if (targetDuration > TimeSpan.Zero && candidate.Duration > TimeSpan.Zero)
        {
            double delta = Math.Abs((candidate.Duration - targetDuration).TotalSeconds);
            if (delta > maxDeltaSec)
            {
                return 0.0;
            }

            durationScore = 1.0 - ((delta / maxDeltaSec) * 0.5);
        }

        // 2. Пересечение токенов названия (порог смягчён до 35% для длинных названий с подзаголовками)
        double titleOverlap = ComputeTokenOverlap(targetTitleTokens, candidate.Title);
        if (titleOverlap < 0.35)
        {
            return 0.0;
        }

        // 3. Совпадение исполнителя
        double artistOverlap = 0.5;
        if (targetArtistTokens.Count > 0)
        {
            double inAuthor = ComputeTokenOverlap(targetArtistTokens, candidate.Author);
            double inTitle = ComputeTokenOverlap(targetArtistTokens, candidate.Title);
            artistOverlap = Math.Max(inAuthor, inTitle);
        }

        return (titleOverlap * 0.50) + (artistOverlap * 0.25) + (durationScore * 0.25);
    }

    private static double ComputeTokenOverlap(HashSet<string> targetTokens, string candidateText)
    {
        if (targetTokens.Count == 0) return 1.0;
        if (string.IsNullOrWhiteSpace(candidateText)) return 0.0;

        int matches = 0;
        foreach (var token in targetTokens)
        {
            if (candidateText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
        }

        return (double)matches / targetTokens.Count;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var parts = text.Split(
            [' ', '\t', '\r', '\n', '-', '—', '–', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '`', '’', '‘', '“', '”', '«', '»', '/', '\\', '&', '+'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length >= 2 && !IsNoiseToken(p))
            {
                tokens.Add(p);
            }
        }

        return tokens;
    }

    private static bool IsNoiseToken(string token)
    {
        return token.Equals("feat", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ft", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("the", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("official", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
