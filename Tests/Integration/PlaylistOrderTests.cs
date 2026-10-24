using System.Diagnostics;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Набор тестов и диагностических зондов порядка следования треков в плейлистах,
/// целостности идентификаторов SetVideoId и CRUD-операций.
/// </summary>
public static class PlaylistOrderTests
{
    [TestMethod(TestCategory.Integration, "SetVideoId Extraction Integrity",
        Group = TestGroups.Playlists, Order = 1, RequiresNetwork = true, TimeoutSeconds = 45)]
    public static async Task TestSetVideoIdExtractionLiveAsync(IServiceProvider services)
    {
        var auth = services.GetRequiredService<CookieAuthService>();
        if (!auth.IsAuthenticated)
        {
            TestAssert.Skip("User is not authenticated in YouTube Music");
        }

        var youtube = services.GetRequiredService<YoutubeProvider>();
        var userPlaylists = await youtube.GetUserPlaylistsByAuthAsync().ConfigureAwait(false);

        var targetPlaylist = userPlaylists.Find(p => !string.IsNullOrEmpty(p.YoutubeId));
        if (targetPlaylist is null)
        {
            TestAssert.Skip("No cloud playlists found on authenticated account");
        }

        var fullData = await youtube.GetFullPlaylistDataAsync(targetPlaylist.YoutubeId!).ConfigureAwait(false);
        TestAssert.NotNull(fullData, $"GetFullPlaylistDataAsync returned null for playlist '{targetPlaylist.YoutubeId}'");
        TestAssert.True(fullData!.Tracks.Count > 0, $"No tracks returned for playlist '{targetPlaylist.YoutubeId}'");

        int missingCount = 0;
        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            if (string.IsNullOrEmpty(fullData.Tracks[i].SetVideoId))
                missingCount++;
        }

        Log.Info($"[PlaylistOrderTests] Playlist '{fullData.Title}': Total tracks={fullData.Tracks.Count}, Valid SetVideoIds={fullData.Tracks.Count - missingCount}");
        TestAssert.Equal(0, missingCount, $"{missingCount} of {fullData.Tracks.Count} tracks have an empty SetVideoId");
    }

    [TestMethod(TestCategory.Integration, "Registry Order Preservation",
        Group = TestGroups.Playlists, Order = 2, RequiresNetwork = false, TimeoutSeconds = 15)]
    public static async Task TestRegistryOrderPreservationAsync(IServiceProvider services)
    {
        var playlistService = services.GetRequiredService<PlaylistService>();

        var playlists = await playlistService.GetAllPlaylistsWithCountsAsync().ConfigureAwait(false);
        var target = playlists.Find(p => p.TrackCount > 1);

        if (target.Playlist is null)
        {
            TestAssert.Skip("No local playlists with > 1 tracks available for ordering test");
        }

        var playlistId = target.Playlist.Id;
        var expectedIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
        var hydratedTracks = await playlistService.GetPlaylistTracksAsync(playlistId).ConfigureAwait(false);

        TestAssert.Equal(expectedIds.Count, hydratedTracks.Count, "Track count mismatch between IDs and hydrated objects");

        for (int i = 0; i < expectedIds.Count; i++)
        {
            TestAssert.Equal(expectedIds[i], hydratedTracks[i].Id, $"Order corruption at index {i}");
        }

        Log.Info($"[PlaylistOrderTests] Registry order verified for playlist '{target.Playlist.Name}' ({expectedIds.Count} tracks).");
    }

    [TestMethod(TestCategory.Integration, "Liked Tracks API Order",
        Group = TestGroups.Playlists, Order = 3, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestLikedTracksApiOrderAsync(IServiceProvider services)
    {
        var auth = services.GetRequiredService<CookieAuthService>();
        if (!auth.IsAuthenticated)
        {
            TestAssert.Skip("User is not authenticated in YouTube Music");
        }

        var youtubeProvider = services.GetRequiredService<YoutubeProvider>();
        var userData = services.GetRequiredService<YoutubeUserDataService>();
        var likedTracks = await userData.GetLikedTracksAsync(youtubeProvider, LikeSyncMode.MusicOnly).ConfigureAwait(false);

        TestAssert.True(likedTracks.Count > 0, "Liked tracks API returned empty list for authenticated user");

        int inspectLimit = Math.Min(5, likedTracks.Count);
        for (int i = 0; i < inspectLimit; i++)
        {
            var t = likedTracks[i];
            Log.Info($"  [{i}] ID={t.Id} | Title='{t.Title}' | Author='{t.Author}'");
        }
    }

    [TestMethod(TestCategory.Integration, "Liked Monotonic Ordering",
        Group = TestGroups.Playlists, Order = 4, RequiresNetwork = false, TimeoutSeconds = 15)]
    public static async Task TestLikedTracksMonotonicOrderingAsync(IServiceProvider services)
    {
        var playlistService = services.GetRequiredService<PlaylistService>();

        var testTracks = new List<TrackInfo>
        {
            new() { Id = "yt_test_like_alpha", Title = "Alpha (Newest)", Author = "Test" },
            new() { Id = "yt_test_like_beta", Title = "Beta (Middle)", Author = "Test" },
            new() { Id = "yt_test_like_gamma", Title = "Gamma (Oldest)", Author = "Test" }
        };

        try
        {
            await playlistService.AddTracksToPlaylistAsync(LibraryService.LikedPlaylistId, testTracks).ConfigureAwait(false);

            var trackIds = await playlistService.GetPlaylistTrackIdsAsync(LibraryService.LikedPlaylistId).ConfigureAwait(false);

            int idxAlpha = trackIds.IndexOf("yt_test_like_alpha");
            int idxBeta = trackIds.IndexOf("yt_test_like_beta");
            int idxGamma = trackIds.IndexOf("yt_test_like_gamma");

            TestAssert.True(idxAlpha >= 0 && idxBeta >= 0 && idxGamma >= 0, "Inserted test tracks not found in Liked playlist");

            bool isMonotonic = (idxAlpha < idxBeta && idxBeta < idxGamma) || (idxGamma < idxBeta && idxBeta < idxAlpha);
            TestAssert.True(isMonotonic, $"Expected monotonic order for Liked tracks, got Alpha={idxAlpha}, Beta={idxBeta}, Gamma={idxGamma}");
        }
        finally
        {
            for (int i = 0; i < testTracks.Count; i++)
            {
                await playlistService.RemoveTrackFromPlaylistAsync(LibraryService.LikedPlaylistId, testTracks[i].Id).ConfigureAwait(false);
            }
        }
    }

    [TestMethod(TestCategory.Integration, "Local CRUD Order & Compaction",
        Group = TestGroups.Playlists, Order = 5, RequiresNetwork = false, TimeoutSeconds = 15)]
    public static async Task TestPlaylistCrudOrderAndCompactionAsync(IServiceProvider services)
    {
        var playlistService = services.GetRequiredService<PlaylistService>();

        var playlist = await playlistService.CreatePlaylistAsync("LMP_Automated_Order_Test").ConfigureAwait(false);
        var playlistId = playlist.Id;

        var tA = new TrackInfo { Id = "yt_crud_track_a", Title = "Track A", Author = "Author" };
        var tB = new TrackInfo { Id = "yt_crud_track_b", Title = "Track B", Author = "Author" };
        var tC = new TrackInfo { Id = "yt_crud_track_c", Title = "Track C", Author = "Author" };

        try
        {
            await playlistService.AddTrackToPlaylistAsync(playlistId, tA).ConfigureAwait(false);
            await playlistService.AddTrackToPlaylistAsync(playlistId, tB).ConfigureAwait(false);
            await playlistService.AddTrackToPlaylistAsync(playlistId, tC).ConfigureAwait(false);

            var initialIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
            TestAssert.Equal(3, initialIds.Count);
            TestAssert.Equal(tA.Id, initialIds[0]);
            TestAssert.Equal(tB.Id, initialIds[1]);
            TestAssert.Equal(tC.Id, initialIds[2]);

            await playlistService.MovePlaylistTrackAsync(playlistId, 0, 1).ConfigureAwait(false);
            var movedIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
            TestAssert.Equal(tB.Id, movedIds[0]);
            TestAssert.Equal(tA.Id, movedIds[1]);
            TestAssert.Equal(tC.Id, movedIds[2]);

            await playlistService.RemoveTrackFromPlaylistAsync(playlistId, tA.Id).ConfigureAwait(false);
            var afterRemoveIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
            TestAssert.Equal(2, afterRemoveIds.Count);
            TestAssert.Equal(tB.Id, afterRemoveIds[0]);
            TestAssert.Equal(tC.Id, afterRemoveIds[1]);
        }
        finally
        {
            await playlistService.DeletePlaylistAsync(playlistId).ConfigureAwait(false);
        }
    }

    [TestMethod(TestCategory.Diagnostic, "Diagnose Playlist Order & API Sync",
        Group = TestGroups.Playlists, Order = 99, RequiresNetwork = false, TimeoutSeconds = 30)]
    public static Task DiagnosePlaylistOrderDiagnosticAsync(IServiceProvider services) =>
        DiagnosePlaylistOrderAsync(services, playlistUrl: null);

    public static async Task DiagnosePlaylistOrderAsync(IServiceProvider services, string? playlistUrl = null)
    {
        var sw = Stopwatch.StartNew();
        var playlistService = services.GetRequiredService<PlaylistService>();

        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            var likedIds = await playlistService.GetPlaylistTrackIdsAsync(LibraryService.LikedPlaylistId).ConfigureAwait(false);
            var likedTracks = await playlistService.GetPlaylistTracksAsync(LibraryService.LikedPlaylistId).ConfigureAwait(false);

            Log.Info($"[Diagnostic] Liked IDs count: {likedIds.Count} | Hydrated count: {likedTracks.Count}");
            int compareCount = Math.Min(likedIds.Count, likedTracks.Count);
            int mismatches = 0;

            for (int i = 0; i < compareCount; i++)
            {
                if (!string.Equals(likedIds[i], likedTracks[i].Id, StringComparison.Ordinal))
                {
                    mismatches++;
                    if (mismatches <= 5)
                    {
                        Log.Warn($"  MISMATCH [{i}]: Expected '{likedIds[i]}', Got '{likedTracks[i].Id}'");
                    }
                }
            }

            sw.Stop();
            if (mismatches == 0)
                Log.Info($"[Diagnostic] ✓ Liked order strictly verified in {sw.ElapsedMilliseconds}ms");
            else
                TestAssert.Fail($"Total mismatches: {mismatches} of {compareCount} tracks corrupted");
        }
        else
        {
            var youtube = services.GetRequiredService<YoutubeProvider>();
            var res = await youtube.GetPlaylistAsync(playlistUrl).ConfigureAwait(false);

            if (res == null)
            {
                TestAssert.Fail($"Failed to load playlist from YouTube: {playlistUrl}");
            }
            else
            {
                var (plName, plTracks) = res.Value;
                Log.Info($"[Diagnostic] Loaded '{plName}' with {plTracks.Count} tracks in {sw.ElapsedMilliseconds}ms");
            }
        }
    }
}