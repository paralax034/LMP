namespace LMP.UI.Helpers;

/// <summary>
/// Extensions for integrating AudioEngine with the notification UI layer.
/// </summary>
public static class AudioEngineExtensions
{
    /// <summary>
    /// Safely adds a list of tracks to the queue with duplicate filtering and displays a toast hint.
    /// </summary>
    /// <param name="audio">The audio engine instance.</param>
    /// <param name="tracks">The list of tracks to add.</param>
    /// <param name="playlistName">The source (playlist) name for logging.</param>
    public static void EnqueuePlaylistWithNotification(
        this AudioEngine audio,
        IEnumerable<TrackInfo> tracks,
        string playlistName)
    {
        var trackList = tracks as List<TrackInfo> ?? [.. tracks];
        if (trackList.Count == 0) return;

        int addedCount = audio.EnqueueRangeUnique(trackList);
        int skippedCount = trackList.Count - addedCount;

        var L = LocalizationService.Instance;

        if (addedCount > 0)
        {
            if (skippedCount > 0)
            {
                Log.Info($"[Queue] Added {addedCount} unique tracks from '{playlistName}', skipped {skippedCount} duplicates.");
                CopyHintService.Instance.Show(
                    string.Format(L["Playlist_AddedWithDuplicatesSkipped"], 
                        addedCount, skippedCount),
                    CopyHintKind.Success);
            }
            else
            {
                Log.Info($"[Queue] Added all {addedCount} tracks from '{playlistName}' to queue.");
                CopyHintService.Instance.Show(
                    string.Format(L["Playlist_AddedToQueue"], addedCount),
                    CopyHintKind.Success);
            }
        }
        else
        {
            Log.Debug($"[Queue] All tracks from '{playlistName}' are already present in the queue.");
            CopyHintService.Instance.Show(
                L["Playlist_AllAlreadyInQueue"],
                CopyHintKind.Warning);
        }
    }
}