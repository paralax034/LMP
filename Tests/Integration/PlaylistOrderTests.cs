using System.Diagnostics;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Диагностический и регрессионный набор тестов порядка следования треков,
/// корректности извлечения <c>setVideoId</c>, CRUD-операций и синхронизации плейлистов через <see cref="PlaylistService"/>.
/// </summary>
public static class PlaylistOrderTests
{
    /// <summary>
    /// Проверяет, что при выборке плейлиста из YouTube Music API для каждого трека
    /// успешно парсится уникальный идентификатор вхождения (<c>SetVideoId</c>).
    /// Пустой <c>SetVideoId</c> приводит к невозможности точечного удаления треков и ошибкам при замене плейлиста в облаке.
    /// </summary>
    /// <param name="services">DI-контейнер приложения.</param>
    /// <returns>Асинхронная задача выполнения теста.</returns>
    [TestMethod(TestCategory.Integration, "Playlist: SetVideoId Extraction Integrity", Group = TestGroups.Pipeline, Order = 48, RequiresNetwork = true)]
    public static async Task TestSetVideoIdExtractionLiveAsync(IServiceProvider services)
    {
        var auth = services.GetRequiredService<CookieAuthService>();
        if (!auth.IsAuthenticated)
        {
            Log.Warn("[PlaylistOrderTests] Skipped TestSetVideoIdExtractionLiveAsync: user is not authenticated.");
            return;
        }

        var youtube = services.GetRequiredService<YoutubeProvider>();
        var userPlaylists = await youtube.GetUserPlaylistsByAuthAsync().ConfigureAwait(false);

        var targetPlaylist = userPlaylists.Find(p => !string.IsNullOrEmpty(p.YoutubeId));
        if (targetPlaylist is null)
        {
            Log.Warn("[PlaylistOrderTests] Skipped: no cloud playlists found on authenticated account.");
            return;
        }

        var fullData = await youtube.GetFullPlaylistDataAsync(targetPlaylist.YoutubeId!).ConfigureAwait(false);
        Assert(fullData != null, $"GetFullPlaylistDataAsync returned null for playlist '{targetPlaylist.YoutubeId}'");
        Assert(fullData!.Tracks.Count > 0, $"No tracks returned for playlist '{targetPlaylist.YoutubeId}'");

        int missingCount = 0;
        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var t = fullData.Tracks[i];
            if (string.IsNullOrEmpty(t.SetVideoId))
                missingCount++;
        }

        Log.Info($"[PlaylistOrderTests] Playlist '{fullData.Title}': Total tracks={fullData.Tracks.Count}, Valid SetVideoIds={fullData.Tracks.Count - missingCount}");
        Assert(missingCount == 0, $"{missingCount} of {fullData.Tracks.Count} tracks have an empty SetVideoId! Cloud deletion will fail.");
    }

    /// <summary>
    /// Проверяет строгий инвариант порядка выборки: <see cref="PlaylistService.GetPlaylistTracksAsync(string, CancellationToken)"/>
    /// обязан возвращать объекты треков в идентичной последовательности, что и <see cref="PlaylistService.GetPlaylistTrackIdsAsync(string, CancellationToken)"/>.
    /// </summary>
    /// <param name="services">DI-контейнер приложения.</param>
    /// <returns>Асинхронная задача выполнения теста.</returns>
    [TestMethod(TestCategory.Integration, "Playlist: Registry Order Preservation", Group = TestGroups.Pipeline, Order = 50)]
    public static async Task TestRegistryOrderPreservationAsync(IServiceProvider services)
    {
        var playlistService = services.GetRequiredService<PlaylistService>();

        var playlists = await playlistService.GetAllPlaylistsWithCountsAsync().ConfigureAwait(false);
        var target = playlists.Find(p => p.TrackCount > 1);

        if (target.Playlist is null)
        {
            Log.Warn("[PlaylistOrderTests] Skipped TestRegistryOrderPreservationAsync: no local playlists with > 1 tracks.");
            return;
        }

        var playlistId = target.Playlist.Id;
        var expectedIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
        var hydratedTracks = await playlistService.GetPlaylistTracksAsync(playlistId).ConfigureAwait(false);

        Assert(expectedIds.Count == hydratedTracks.Count,
            $"Track count mismatch: IDs={expectedIds.Count}, Hydrated={hydratedTracks.Count}");

        for (int i = 0; i < expectedIds.Count; i++)
        {
            var expectedId = expectedIds[i];
            var actualId = hydratedTracks[i].Id;

            Assert(string.Equals(expectedId, actualId, StringComparison.Ordinal),
                $"Order corruption at index {i}: Expected track ID '{expectedId}', but got '{actualId}'");
        }

        Log.Info($"[PlaylistOrderTests] Registry order verified successfully for playlist '{target.Playlist.Name}' ({expectedIds.Count} tracks).");
    }

    /// <summary>
    /// Проверяет порядок выдачи треков из облачного плейлиста "Понравившиеся" (LM / LL) через YouTube API.
    /// </summary>
    /// <param name="services">DI-контейнер приложения.</param>
    /// <returns>Асинхронная задача выполнения теста.</returns>
    [TestMethod(TestCategory.Integration, "Playlist: Liked Tracks API Order", Group = TestGroups.Pipeline, Order = 51, RequiresNetwork = true)]
    public static async Task TestLikedTracksApiOrderAsync(IServiceProvider services)
    {
        var auth = services.GetRequiredService<CookieAuthService>();
        if (!auth.IsAuthenticated)
        {
            Log.Warn("[PlaylistOrderTests] Skipped TestLikedTracksApiOrderAsync: user is not authenticated.");
            return;
        }

        var youtubeProvider = services.GetRequiredService<YoutubeProvider>();
        var userData = services.GetRequiredService<YoutubeUserDataService>();
        var likedTracks = await userData.GetLikedTracksAsync(youtubeProvider, LikeSyncMode.MusicOnly).ConfigureAwait(false);

        Assert(likedTracks.Count > 0, "Liked tracks API returned empty list for authenticated user.");

        Log.Info($"[PlaylistOrderTests] Fetched {likedTracks.Count} liked tracks. Inspecting first 5 (newest expected):");
        int inspectLimit = Math.Min(5, likedTracks.Count);
        for (int i = 0; i < inspectLimit; i++)
        {
            var t = likedTracks[i];
            Log.Info($"  [{i}] ID={t.Id} | Title='{t.Title}' | Author='{t.Author}'");
        }
    }

    /// <summary>
    /// Проверяет, что пакетное добавление треков в системный плейлист "Liked" назначает монотонно убывающие метки времени,
    /// гарантируя, что первый переданный трек (самый свежий) всегда остаётся на позиции 0 при сортировке <c>LikedAt DESC</c>.
    /// </summary>
    /// <param name="services">DI-контейнер приложения.</param>
    /// <returns>Асинхронная задача выполнения теста.</returns>
    [TestMethod(TestCategory.Integration, "Playlist: Liked Monotonic Ordering", Group = TestGroups.Pipeline, Order = 53)]
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

            Assert(idxAlpha >= 0 && idxBeta >= 0 && idxGamma >= 0, "Inserted test tracks were not found in Liked playlist.");
            Assert(idxAlpha < idxBeta, $"Expected Alpha before Beta, but Alpha={idxAlpha}, Beta={idxBeta}");
            Assert(idxBeta < idxGamma, $"Expected Beta before Gamma, but Beta={idxBeta}, Gamma={idxGamma}");

            Log.Info("[PlaylistOrderTests] Monotonic Liked timestamp order verified successfully.");
        }
        finally
        {
            for (int i = 0; i < testTracks.Count; i++)
            {
                await playlistService.RemoveTrackFromPlaylistAsync(LibraryService.LikedPlaylistId, testTracks[i].Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Тестирует полный цикл локальных CRUD-операций через <see cref="PlaylistService"/> с проверкой сохранения порядка:
    /// создание плейлиста, добавление треков, перемещение (0 → 1), удаление трека и удаление плейлиста.
    /// </summary>
    /// <param name="services">DI-контейнер приложения.</param>
    /// <returns>Асинхронная задача выполнения теста.</returns>
    [TestMethod(TestCategory.Integration, "Playlist: Local CRUD Order & Compaction", Group = TestGroups.Pipeline, Order = 54)]
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
            Assert(initialIds.Count == 3, $"Expected 3 tracks, got {initialIds.Count}");
            Assert(initialIds[0] == tA.Id && initialIds[1] == tB.Id && initialIds[2] == tC.Id, "Initial insertion order violated.");

            // Перемещаем элемент 0 на позицию 1: ожидаем [B, A, C]
            await playlistService.MovePlaylistTrackAsync(playlistId, 0, 1).ConfigureAwait(false);
            var movedIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
            Assert(movedIds[0] == tB.Id && movedIds[1] == tA.Id && movedIds[2] == tC.Id,
                $"Move failed. Expected [B, A, C], got [{movedIds[0]}, {movedIds[1]}, {movedIds[2]}]");

            // Удаляем средний элемент A: ожидаем [B, C] без пробелов в индексах
            await playlistService.RemoveTrackFromPlaylistAsync(playlistId, tA.Id).ConfigureAwait(false);
            var afterRemoveIds = await playlistService.GetPlaylistTrackIdsAsync(playlistId).ConfigureAwait(false);
            Assert(afterRemoveIds.Count == 2, $"Expected 2 tracks after removal, got {afterRemoveIds.Count}");
            Assert(afterRemoveIds[0] == tB.Id && afterRemoveIds[1] == tC.Id,
                $"Removal compaction failed. Expected [B, C], got [{afterRemoveIds[0]}, {afterRemoveIds[1]}]");

            Log.Info("[PlaylistOrderTests] Local CRUD order & compaction test passed.");
        }
        finally
        {
            await playlistService.DeletePlaylistAsync(playlistId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Полная диагностическая утилита. Выводит сопоставление порядка треков в консоль и системный лог.
    /// </summary>
    /// <param name="services">DI-контейнер приложения.</param>
    /// <param name="playlistUrl">Опциональный URL плейлиста для проверки (если null — проверяется локальный Liked).</param>
    /// <returns>Асинхронная задача выполнения диагностики.</returns>
    public static async Task DiagnosePlaylistOrderAsync(IServiceProvider services, string? playlistUrl = null)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine("  PLAYLIST ORDER DIAGNOSTIC SUITE");
        Console.WriteLine(new string('=', 70));

        var playlistService = services.GetRequiredService<PlaylistService>();

        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            Console.WriteLine("\n[1/2] Auditing Local 'Liked' Playlist IDs vs Hydrated Objects...");
            var likedIds = await playlistService.GetPlaylistTrackIdsAsync(LibraryService.LikedPlaylistId).ConfigureAwait(false);
            var likedTracks = await playlistService.GetPlaylistTracksAsync(LibraryService.LikedPlaylistId).ConfigureAwait(false);

            Console.WriteLine($"Total Liked IDs: {likedIds.Count} | Total Hydrated: {likedTracks.Count}");
            int compareCount = Math.Min(likedIds.Count, likedTracks.Count);
            int mismatches = 0;

            for (int i = 0; i < compareCount; i++)
            {
                if (!string.Equals(likedIds[i], likedTracks[i].Id, StringComparison.Ordinal))
                {
                    mismatches++;
                    if (mismatches <= 5)
                    {
                        Console.WriteLine($"  MISMATCH at [{i}]: Expected ID '{likedIds[i]}', Got '{likedTracks[i].Id}' ('{likedTracks[i].Title}')");
                    }
                }
            }

            if (mismatches == 0)
                Console.WriteLine("  ✓ Local Liked playlist IDs strictly match hydrated track order.");
            else
                Console.WriteLine($"  ✗ TOTAL MISMATCHES: {mismatches} of {compareCount} tracks corrupted in sequence!");
        }
        else
        {
            Console.WriteLine($"\n[1/2] Auditing External Playlist URL: {playlistUrl}");
            var youtube = services.GetRequiredService<YoutubeProvider>();
            var res = await youtube.GetPlaylistAsync(playlistUrl).ConfigureAwait(false);

            if (res == null)
            {
                Console.WriteLine("  ✗ Failed to load playlist from YouTube.");
            }
            else
            {
                var (plName, plTracks) = res.Value;
                Console.WriteLine($"  Loaded '{plName}' with {plTracks.Count} tracks.");
                for (int i = 0; i < Math.Min(10, plTracks.Count); i++)
                {
                    Console.WriteLine($"    [{i + 1:D2}] {plTracks[i].Id} — {plTracks[i].Author} - {plTracks[i].Title}");
                }
            }
        }

        sw.Stop();
        Console.WriteLine($"\nDiagnostic complete in {sw.ElapsedMilliseconds}ms\n" + new string('=', 70));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[PlaylistOrderAssertionFailed] {message}");
    }
}