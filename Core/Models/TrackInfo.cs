using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Normalization;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Utils;
using MemoryPack;

namespace LMP.Core.Models;

/// <summary>
/// Представляет музыкальный трек.
/// string interning для ID, минимизированы аллокации.
/// </summary>
[MemoryPackable]
public sealed partial class TrackInfo : ObservableObject, IBatchItem, ISearchResult
{
    [MemoryPackIgnore]
    private static readonly ConditionalWeakTable<string, string> _idCache = [];

    #region Identity

    /// <summary>
    /// Уникальный идентификатор с кэшированием для избежания дубликатов строк.
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
    /// Извлекает чистый YouTube ID без префикса (zero-alloc через Span).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetRawIdSpan() => YoutubeIdHelper.ExtractRawIdSpan(Id);

    /// <summary>
    /// Получает чистый ID как строку (для async контекста).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetRawId() => YoutubeIdHelper.ExtractRawId(Id);

    #endregion

    #region Metadata

    /// <summary>
    /// Название трека.
    /// </summary>
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;

    /// <summary>
    /// Исполнитель/автор.
    /// </summary>
    [ObservableProperty] public partial string Author { get; set; } = string.Empty;

    /// <summary>
    /// ID канала YouTube.
    /// </summary>
    [ObservableProperty] public partial string? ChannelId { get; set; }

    /// <summary>
    /// URL трека на YouTube.
    /// </summary>
    [ObservableProperty] public partial string Url { get; set; } = string.Empty;

    /// <summary>
    /// Длительность трека.
    /// </summary>
    [ObservableProperty] public partial TimeSpan Duration { get; set; }

    /// <summary>
    /// URL обложки.
    /// </summary>
    [ObservableProperty] public partial string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Флаг официального канала исполнителя.
    /// </summary>
    public bool IsOfficialArtist { get; set; }

    /// <summary>
    /// Флаг музыкального контента.
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
    /// Предпочитаемый формат контейнера для этого трека.
    /// Персистентный пользовательский выбор.
    /// </summary>
    [ObservableProperty] public partial AudioFormat? PreferredFormat { get; set; }

    /// <summary>
    /// Предпочитаемый битрейт для этого трека.
    /// Персистентный пользовательский выбор.
    /// </summary>
    [ObservableProperty] public partial int PreferredBitrate { get; set; }

    public string? RadioSeedId { get; set; }

    #endregion

    #region Runtime Stream Selection

    /// <summary>
    /// Временный формат контейнера, выбранный пользователем в текущей сессии
    /// (например, при переключении качества).
    /// Не является кэшем resolved stream metadata.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public AudioFormat? TransientFormat { get; set; }

    /// <summary>
    /// Временный битрейт, выбранный пользователем в текущей сессии
    /// (например, при переключении качества).
    /// Не является кэшем resolved stream metadata.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public int TransientBitrate { get; set; }

    #endregion

    #region Audio Normalization

    /// <summary>
    /// Новое canonical-поле integrated loudness трека в LUFS.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public float IntegratedLufs { get; set; } = float.NaN;

    /// <summary>
    /// Источник значения <see cref="IntegratedLufs"/>.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public LoudnessSource IntegratedLufsSource { get; set; } = LoudnessSource.Unknown;

    /// <summary>
    /// <c>true</c> если integrated loudness измерена.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public bool HasIntegratedLufs =>
        !float.IsNaN(IntegratedLufs) && float.IsFinite(IntegratedLufs);

    /// <summary>
    /// Устанавливает integrated loudness трека.
    /// Более точный источник может перезаписать менее точный.
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
    /// Обновляет метаданные из свежего объекта.
    /// Игнорирует пустые значения и предотвращает затирание статуса лайка
    /// ответами API, не содержащими пользовательского контекста.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMetadata(TrackInfo fresh)
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

        if (fresh.IsLiked && !IsLiked)
            IsLiked = true;

        if (fresh.IsDisliked && !IsDisliked)
            IsDisliked = true;

        if (fresh.HasIntegratedLufs)
            SetIntegratedLufs(fresh.IntegratedLufs, fresh.IntegratedLufsSource);

        if (fresh.TransientFormat is { } transientFormat
            && transientFormat != AudioFormat.Unknown
            && transientFormat != TransientFormat)
        {
            TransientFormat = transientFormat;
        }

        if (fresh.TransientBitrate > 0 && fresh.TransientBitrate != TransientBitrate)
            TransientBitrate = fresh.TransientBitrate;
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