using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Normalization;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Utils;
using MemoryPack;

namespace LMP.Core.Models;

/// <summary>
/// Represents a music track.
/// String interning for IDs, allocations minimized.
/// </summary>
[MemoryPackable]
public sealed partial class TrackInfo : ObservableObject, IBatchItem, ISearchResult
{
    [MemoryPackIgnore]
    private static readonly ConditionalWeakTable<string, string> _idCache = [];

    #region Identity

    /// <summary>
    /// Unique identifier with caching to prevent duplicate strings.
    /// </summary>
    public string Id
    {
        get;
        set
        {
            if (field == value) return;

            if (!string.IsNullOrEmpty(value))
            {
                if (value.StartsWith("yt_") || value.StartsWith("yt_pl_"))
                {
                    field = value;
                }
                else
                {
                    field = GetCachedPrefixedId("yt_", value);
                }
            }
            else
            {
                field = value ?? string.Empty;
            }

            OnPropertyChanged();
        }
    } = string.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetCachedPrefixedId(string prefix, string rawId)
    {
        if (!_idCache.TryGetValue(rawId, out var cached))
        {
            cached = string.Concat(prefix, rawId);
            _idCache.AddOrUpdate(rawId, cached);
        }
        return cached;
    }

    /// <summary>
    /// Extracts raw YouTube ID without prefix (zero-alloc via Span).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetRawIdSpan() => YoutubeIdHelper.ExtractRawIdSpan(Id);

    /// <summary>
    /// Gets raw ID as a string (for async context).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetRawId() => YoutubeIdHelper.ExtractRawId(Id);

    #endregion

    #region Metadata

    /// <summary>
    /// Track title.
    /// </summary>
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;

    /// <summary>
    /// Artist/author.
    /// </summary>
    [ObservableProperty] public partial string Author { get; set; } = string.Empty;

    /// <summary>
    /// YouTube channel ID.
    /// </summary>
    [ObservableProperty] public partial string? ChannelId { get; set; }

    /// <summary>
    /// YouTube track URL.
    /// </summary>
    [ObservableProperty] public partial string Url { get; set; } = string.Empty;

    /// <summary>
    /// Track duration.
    /// </summary>
    [ObservableProperty] public partial TimeSpan Duration { get; set; }

    /// <summary>
    /// Thumbnail URL.
    /// </summary>
    [ObservableProperty] public partial string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Official artist channel flag.
    /// </summary>
    public bool IsOfficialArtist { get; set; }

    /// <summary>
    /// Music content flag.
    /// </summary>
    public bool IsMusic { get; set; }

    [JsonIgnore]
    [MemoryPackIgnore]
    public bool IsExplicitVideoClip => IsOfficialArtist && !IsMusic;

    [JsonIgnore]
    [MemoryPackIgnore]
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    #endregion

    #region User State

    [ObservableProperty] public partial bool IsLiked { get; set; }
    [ObservableProperty] public partial bool IsDisliked { get; set; }
    [ObservableProperty] public partial bool IsDownloaded { get; set; }
    [ObservableProperty] public partial bool IsCached { get; set; }

    [JsonIgnore]
    [MemoryPackIgnore]
    public bool IsAvailableOffline => IsDownloaded || IsCached;

    [ObservableProperty] public partial string? LocalPath { get; set; }

    #endregion

    #region Playlists

    public HashSet<string> InPlaylists
    {
        get => field ??= new HashSet<string>(StringComparer.Ordinal);
        set;
    }

    #endregion

    #region Format Preferences

    /// <summary>
    /// Preferred container format for this track.
    /// Persistent user selection.
    /// </summary>
    [ObservableProperty] public partial AudioFormat? PreferredFormat { get; set; }

    /// <summary>
    /// Preferred bitrate for this track.
    /// Persistent user selection.
    /// </summary>
    [ObservableProperty] public partial int PreferredBitrate { get; set; }

    /// <summary>
    /// Per-track custom equalizer preset.
    /// <c>null</c> = use global equalizer profile.
    /// </summary>
    [ObservableProperty] public partial string? EqualizerPresetId { get; set; }

    public string? RadioSeedId { get; set; }

    #endregion

    #region Runtime Stream Selection

    /// <summary>
    /// Transient container format selected by the user in the current session
    /// (e.g., when switching quality).
    /// Is not a cache of resolved stream metadata.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public AudioFormat? TransientFormat { get; set; }

    /// <summary>
    /// Transient bitrate selected by the user in the current session
    /// (e.g., when switching quality).
    /// Is not a cache of resolved stream metadata.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public int TransientBitrate { get; set; }

    #endregion

    #region Audio Normalization

    /// <summary>
    /// New canonical field for track integrated loudness in LUFS.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public float IntegratedLufs { get; set; } = float.NaN;

    /// <summary>
    /// Source of the <see cref="IntegratedLufs"/> value.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public LoudnessSource IntegratedLufsSource { get; set; } = LoudnessSource.Unknown;

    /// <summary>
    /// <c>true</c> if integrated loudness is measured.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public bool HasIntegratedLufs =>
        !float.IsNaN(IntegratedLufs) && float.IsFinite(IntegratedLufs);

    /// <summary>
    /// Sets the track's integrated loudness.
    /// A more accurate source can overwrite a less accurate one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetIntegratedLufs(float lufs, LoudnessSource source)
    {
        if (!float.IsFinite(lufs))
            return false;

        if (IntegratedLufsSource > source)
            return false;

        if (HasIntegratedLufs
            && MathF.Abs(IntegratedLufs - lufs) < 0.01f
            && IntegratedLufsSource == source)
        {
            return false;
        }

        IntegratedLufs = lufs;
        IntegratedLufsSource = source;
        return true;
    }

    #endregion

    #region Constructors

    public TrackInfo() { }

    #endregion

    #region Methods

    /// <summary>
    /// Updates metadata from a fresh object.
    /// Ignores empty values and prevents overwriting the like status
    /// by API responses that lack user context.
    /// </summary>
    /// <param name="fresh">A fresh track instance containing updated metadata.</param>
    /// <param name="includeUserState">Specifies whether the incoming object contains authorized user context (likes, local path, download status).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMetadata(TrackInfo fresh, bool includeUserState = false)
    {
        if (!string.IsNullOrEmpty(fresh.Title) && fresh.Title != Title)
            Title = fresh.Title;

        if (!string.IsNullOrEmpty(fresh.Author) && fresh.Author != Author)
            Author = fresh.Author;

        if (!string.IsNullOrEmpty(fresh.Url) && fresh.Url != Url)
            Url = fresh.Url;

        if (!string.IsNullOrEmpty(fresh.ThumbnailUrl) && fresh.ThumbnailUrl != ThumbnailUrl)
            ThumbnailUrl = fresh.ThumbnailUrl;

        if (fresh.Duration.TotalSeconds > 0 && fresh.Duration != Duration)
            Duration = fresh.Duration;

        if (fresh.IsOfficialArtist && !IsOfficialArtist)
            IsOfficialArtist = true;

        if (fresh.IsMusic && !IsMusic)
            IsMusic = true;

        if (!string.IsNullOrEmpty(fresh.ChannelId) && fresh.ChannelId != ChannelId)
            ChannelId = fresh.ChannelId;

        if (includeUserState)
        {
            if (IsLiked != fresh.IsLiked)
                IsLiked = fresh.IsLiked;

            if (IsDisliked != fresh.IsDisliked)
                IsDisliked = fresh.IsDisliked;

            if (IsDownloaded != fresh.IsDownloaded)
                IsDownloaded = fresh.IsDownloaded;

            if (!string.IsNullOrEmpty(fresh.LocalPath) && fresh.LocalPath != LocalPath)
                LocalPath = fresh.LocalPath;

            if (!InPlaylists.SetEquals(fresh.InPlaylists))
            {
                InPlaylists.Clear();
                foreach (var playlistId in fresh.InPlaylists)
                    InPlaylists.Add(playlistId);
            }
        }
        else
        {
            if (fresh.IsLiked && !IsLiked)
                IsLiked = true;

            if (fresh.IsDisliked && !IsDisliked)
                IsDisliked = true;
        }

        if (fresh.HasIntegratedLufs)
            SetIntegratedLufs(fresh.IntegratedLufs, fresh.IntegratedLufsSource);

        if (!string.IsNullOrEmpty(fresh.EqualizerPresetId) && fresh.EqualizerPresetId != EqualizerPresetId)
            EqualizerPresetId = fresh.EqualizerPresetId;

        if (fresh.TransientFormat is { } transientFormat
            && transientFormat != AudioFormat.Unknown
            && transientFormat != TransientFormat)
        {
            TransientFormat = transientFormat;
        }

        if (fresh.TransientBitrate > 0 && fresh.TransientBitrate != TransientBitrate)
            TransientBitrate = fresh.TransientBitrate;
    }

    /// <summary>
    /// Forcibly sets the like status for the canonical instance.
    /// </summary>
    /// <param name="isLiked">Target like status.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLikedState(bool isLiked)
    {
        if (IsLiked == isLiked) return;

        IsLiked = isLiked;
        if (isLiked && IsDisliked)
            IsDisliked = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkAsCached(AudioFormat? format = null, int bitrate = 0)
    {
        IsCached = true;

        if (format is { } preferredFormat && preferredFormat != AudioFormat.Unknown)
            PreferredFormat = preferredFormat;

        if (bitrate > 0)
            PreferredBitrate = bitrate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkAsDownloaded(string localPath, AudioFormat? format = null, int bitrate = 0)
    {
        IsDownloaded = true;
        IsCached = true;
        LocalPath = localPath;

        if (format is { } preferredFormat && preferredFormat != AudioFormat.Unknown)
            PreferredFormat = preferredFormat;

        if (bitrate > 0)
            PreferredBitrate = bitrate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearCacheStatus()
    {
        if (!IsDownloaded)
        {
            IsCached = false;
        }
    }

    #endregion
}