using System.Collections;
using System.Collections.Specialized;
using LMP.UI.Features.Shared;

namespace LMP.UI.ViewModels;

/// <summary>
/// Unified contract for a high-performance virtualized track list.
/// Serves as the Single Source of Truth for element ordering, filtering, and selection state.
/// </summary>
public interface IVirtualTrackList : IList<TrackItemViewModel>, IReadOnlyList<TrackItemViewModel>, IList,
    INotifyCollectionChanged, IDisposable
{
    /// <summary>
    /// Explicitly hides base Count definitions to resolve CS0229 ambiguity.
    /// </summary>
    new int Count { get; }

    /// <summary>
    /// Explicitly hides the indexer to resolve ambiguity between IList and IReadOnlyList (CS0121).
    /// </summary>
    new TrackItemViewModel this[int index] { get; set; }

    /// <summary>
    /// Total number of tracks in the master index (before filtering is applied).
    /// </summary>
    int TotalCount { get; }

    /// <summary>
    /// Number of tracks matching the current filter.
    /// </summary>
    int FilteredCount { get; }

    /// <summary>
    /// Number of currently selected tracks.
    /// </summary>
    int SelectedCount { get; }

    /// <summary>
    /// Playlist context for displaying specific actions in menus and rows.
    /// </summary>
    bool IsPlaylistContext { get; set; }

    /// <summary>
    /// Playback queue context for displaying specific actions in menus and rows.
    /// </summary>
    bool IsQueueContext { get; set; }

    /// <summary>
    /// Delegate provider for a snapshot of selected tracks for the context menu.
    /// </summary>
    Func<IReadOnlyList<TrackInfo>>? SelectionProvider { get; set; }

    /// <summary>
    /// Event raised when the selection set changes for centralized UI notifications.
    /// </summary>
    event Action? SelectionChanged;

    /// <summary>
    /// Checks whether the item at the specified visual index is selected.
    /// </summary>
    bool IsIndexSelected(int visualIndex);

    /// <summary>
    /// Sets the selection state for the item at the specified visual index.
    /// </summary>
    void SetIndexSelected(int visualIndex, bool isSelected);

    /// <summary>
    /// Inverts the selection state for the item at the specified visual index.
    /// </summary>
    void ToggleIndexSelected(int visualIndex);

    /// <summary>
    /// Selects all visible tracks.
    /// </summary>
    void SelectAll();

    /// <summary>
    /// Deselects all items.
    /// </summary>
    void ClearSelection();

    /// <summary>
    /// Selects a contiguous range of visual indices.
    /// </summary>
    void SelectRange(int fromVisualIndex, int toVisualIndex, bool addToExisting);

    /// <summary>
    /// Returns a snapshot of domain models for all selected tracks.
    /// </summary>
    List<TrackInfo> GetSelectedTracks();

    /// <summary>
    /// Returns a snapshot of all master index tracks in their original order (unfiltered).
    /// </summary>
    List<TrackInfo> GetMasterTracks();

    /// <summary>
    /// Retrieves the domain model of a track by its absolute index in the master index (unfiltered).
    /// </summary>
    TrackInfo? GetMasterTrackAt(int masterIndex);

    /// <summary>
    /// Returns an ordered list of selected ViewModels for the viewport.
    /// </summary>
    List<TrackItemViewModel> GetSelectedViewModelsOrdered();

    /// <summary>
    /// Retrieves the domain model of a track by its visual index without creating a ViewModel.
    /// </summary>
    TrackInfo? GetTrackAt(int visualIndex);

    /// <summary>
    /// Returns the visual index of a track by its identifier in O(1)/O(N) without materializing a ViewModel.
    /// </summary>
    int IndexOfTrackId(string? trackId);

    /// <summary>
    /// Returns the active ViewModel instance for a track if it resides in the visible pool.
    /// </summary>
    TrackItemViewModel? TryGetVm(string trackId);

    /// <summary>
    /// Initializes the list with a batch collection of tracks.
    /// </summary>
    void SetTracks(IEnumerable<TrackInfo> tracks, IReadOnlyList<string>? explicitOrder = null,
        bool preserveSelection = false);

    /// <summary>
    /// Appends a batch of tracks to the end of the index.
    /// </summary>
    void AppendTracks(IEnumerable<TrackInfo> newTracks);

    /// <summary>
    /// Applies a search query and executes vectorized SIMD filtering.
    /// </summary>
    void ApplyFilter(string query);

    /// <summary>
    /// Moves an item within the master index and recalculates visual positions.
    /// </summary>
    void MoveTrack(int oldVisualIndex, int newVisualIndex);

    /// <summary>
    /// Removes a track by its identifier.
    /// </summary>
    bool RemoveTrack(string trackId);

    /// <summary>
    /// Updates the active and playback state in O(1).
    /// </summary>
    void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying);

    /// <summary>
    /// Updates physical download progress for the displayed item.
    /// </summary>
    void UpdateDownloadProgress(string trackId, float progress);

    /// <summary>
    /// Commits the completion of a track's physical download.
    /// </summary>
    void UpdateDownloadCompleted(string trackId, bool ok, string? path);

    /// <summary>
    /// Commits the local disk cache status for the item.
    /// </summary>
    void UpdateCacheStatus(string trackId, AudioFormat format, int bitrate);
}
