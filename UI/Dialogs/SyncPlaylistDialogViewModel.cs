namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel диалога синхронизации плейлиста.
/// Показывает diff между локальным и облачным состоянием,
/// позволяет выбрать стратегию и поля для синхронизации.
/// </summary>
public sealed partial class SyncPlaylistDialogViewModel : ViewModelBase
{
    public PlaylistSyncPreview Preview { get; }

    [ObservableProperty]
    public partial int SelectedStrategyIndex { get; set; }

    public PlaylistSyncStrategy SelectedStrategy => SelectedStrategyIndex switch
    {
        0 => PlaylistSyncStrategy.Merge,
        1 => PlaylistSyncStrategy.ReplaceLocal,
        2 => PlaylistSyncStrategy.ReplaceCloud,
        _ => PlaylistSyncStrategy.Merge
    };

    [ObservableProperty]
    public partial bool SyncName { get; set; } = true;

    [ObservableProperty]
    public partial bool SyncDescription { get; set; } = true;

    [ObservableProperty]
    public partial bool SyncThumbnail { get; set; }

    [ObservableProperty]
    public partial bool SyncTracks { get; set; } = true;

    public bool HasNameDiff => Preview.NameDiffers;
    public bool HasDescDiff => Preview.DescriptionDiffers;
    public bool HasTrackDiff => Preview.TracksDiffer;

    /// <summary>
    /// Показывать ли секцию обложек.
    /// Видна когда хотя бы одна из сторон имеет обложку.
    /// </summary>
    public bool HasThumbnailSection =>
        !string.IsNullOrEmpty(Preview.LocalThumbnailUrl) ||
        !string.IsNullOrEmpty(Preview.CloudThumbnailUrl);

    /// <summary>
    /// Нормализованный локальный URL обложки для превью (без query string).
    /// </summary>
    public string? LocalThumbnailPreviewUrl => NormalizeThumbnailUrl(Preview.LocalThumbnailUrl);

    /// <summary>
    /// Нормализованный облачный URL обложки для превью (без query string).
    /// </summary>
    public string? CloudThumbnailPreviewUrl => NormalizeThumbnailUrl(Preview.CloudThumbnailUrl);

    /// <summary>
    /// Обложки отличаются (по нормализованному URL).
    /// </summary>
    public bool ThumbnailDiffers =>
        !string.Equals(
            NormalizeThumbnailUrl(Preview.LocalThumbnailUrl),
            NormalizeThumbnailUrl(Preview.CloudThumbnailUrl),
            StringComparison.OrdinalIgnoreCase);

    public string TrackDiffSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (Preview.CommonTrackCount > 0)
                parts.Add(string.Format(
                    SL["Playlist_SyncCommon"] ?? "common: {0}", Preview.CommonTrackCount));
            if (Preview.LocalOnlyTrackCount > 0)
                parts.Add(string.Format(
                    SL["Playlist_SyncLocalOnly"] ?? "local only: {0}", Preview.LocalOnlyTrackCount));
            if (Preview.CloudOnlyTrackCount > 0)
                parts.Add(string.Format(
                    SL["Playlist_SyncCloudOnly"] ?? "cloud only: {0}", Preview.CloudOnlyTrackCount));
            return string.Join("  •  ", parts);
        }
    }

    public bool CanSyncThumbnail => SelectedStrategy switch
    {
        PlaylistSyncStrategy.ReplaceCloud => !string.IsNullOrEmpty(Preview.LocalThumbnailUrl),
        PlaylistSyncStrategy.Merge or PlaylistSyncStrategy.ReplaceLocal =>
            !string.IsNullOrEmpty(Preview.CloudThumbnailUrl),
        _ => false
    };

    public string StrategyDescription => SelectedStrategy switch
    {
        PlaylistSyncStrategy.Merge =>
            SL["Playlist_SyncStrategy_MergeDesc"] ?? "Add missing tracks to both sides. No deletions.",
        PlaylistSyncStrategy.ReplaceLocal =>
            SL["Playlist_SyncStrategy_ReplaceLocalDesc"]
                ?? "Replace local tracks with YouTube. Local-only tracks will be removed.",
        PlaylistSyncStrategy.ReplaceCloud =>
            SL["Playlist_SyncStrategy_ReplaceCloudDesc"]
                ?? "Replace YouTube tracks with local. Cloud-only tracks will be removed.",
        _ => ""
    };

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<PlaylistSyncOptions?>? OnResult { get; set; }

    public IRelayCommand SyncCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public SyncPlaylistDialogViewModel(PlaylistSyncPreview preview)
    {
        Preview = preview;

        SyncName = preview.NameDiffers;
        SyncDescription = preview.DescriptionDiffers;

        // Thumbnail sync включён только если обложки реально различаются
        // (нормализованное сравнение исключает ложные diff из-за query params)
        SyncThumbnail = ThumbnailDiffers && HasThumbnailSection;

        SyncTracks = preview.TracksDiffer;

        SyncCommand = new RelayCommand(() =>
        {
            var result = new PlaylistSyncOptions
            {
                Strategy = SelectedStrategy,
                SyncName = SyncName,
                SyncDescription = SyncDescription,
                SyncThumbnail = SyncThumbnail,
                SyncTracks = SyncTracks
            };
            OnResult?.Invoke(result);
        });

        CancelCommand = new RelayCommand(() =>
        {
            OnResult?.Invoke(null);
        });
    }

    partial void OnSelectedStrategyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedStrategy));
        OnPropertyChanged(nameof(StrategyDescription));
        OnPropertyChanged(nameof(CanSyncThumbnail));
    }

    /// <summary>
    /// Нормализует URL обложки: убирает query string (sqp=...) для стабильного сравнения.
    /// </summary>
    private static string? NormalizeThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var queryIndex = url.IndexOf('?');
            return queryIndex >= 0 ? url[..queryIndex] : url;
        }

        return url;
    }
}