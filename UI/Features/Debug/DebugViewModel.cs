using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Diagnostics;
using LMP.Core.Youtube.Search;

namespace LMP.UI.Features.Debug;

/// <summary>
/// ViewModel для Debug-окна. Содержит логику YouTube/Memory/Audio вкладок
/// и предоставляет <see cref="TestRunner"/> для вкладки Tests.
/// </summary>
public sealed partial class DebugViewModel : ViewModelBase
{
    private readonly Lazy<YoutubeProvider> _youtubeLazy;
    private YoutubeProvider Youtube => _youtubeLazy.Value;

    // TEST RUNNER (вкладка Tests)

    /// <summary>ViewModel для вкладки тестов. Создаётся лениво при первом обращении.</summary>
    public TestRunnerViewModel TestRunner { get; } = new();

    // EXISTING PROPERTIES (YouTube / Memory / Audio)

    [ObservableProperty]
    public partial string LogOutput { get; set; } = "Debug Session Started...\n";

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "Linkin Park";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string AudioTestInput { get; set; } = "aG_i7fvGSXU";

    [ObservableProperty]
    public partial int AudioTestDuration { get; set; } = 10;

    [ObservableProperty]
    public partial bool IsAudioPlaying { get; set; }

    /// <summary> Активен ли в данный момент фоновый мониторинг зависаний UI-потока. </summary>
    [ObservableProperty]
    public partial bool IsWatchdogEnabled { get; set; }

    /// <summary> Текст состояния для отображения на кнопке управления Watchdog. </summary>
    [ObservableProperty]
    public partial string WatchdogStatusText { get; set; } = "Watchdog: OFF";

    private CancellationTokenSource? _audioTestCts;
    private AudioPlayer? _testPlayer;
    private AudioCacheManager? _testCacheManager;
    private UIHangWatchdog? _uiWatchdog;

    public IAsyncRelayCommand GetLikedVideosCommand { get; }
    public IAsyncRelayCommand GetLikedMusicCommand { get; }
    public IAsyncRelayCommand SearchVideosCommand { get; }
    public IAsyncRelayCommand SearchMusicCommand { get; }
    public IRelayCommand ClearLogCommand { get; }

    public IRelayCommand DumpMemoryCommand { get; }
    public IRelayCommand ForceGcCommand { get; }
    public IAsyncRelayCommand ClearCachesCommand { get; }
    public IRelayCommand CheckVmLeaksCommand { get; }

    public IAsyncRelayCommand PlayYoutubeAudioCommand { get; }
    public IAsyncRelayCommand PlayYoutubeWithCacheCommand { get; }
    public IRelayCommand StopAudioTestCommand { get; }
    public IRelayCommand ShowCacheStatsCommand { get; }
    public IAsyncRelayCommand ClearAudioCacheCommand { get; }
    public IAsyncRelayCommand TestLocalFileCommand { get; }

    public IRelayCommand ToggleWatchdogCommand { get; }

    public DebugViewModel()
    {
        _youtubeLazy = AppEntry.Services.GetRequiredService<Lazy<YoutubeProvider>>();

        GetLikedVideosCommand = new AsyncRelayCommand(ExecuteGetLikedVideos);
        GetLikedMusicCommand = new AsyncRelayCommand(ExecuteGetLikedMusic);
        SearchVideosCommand = new AsyncRelayCommand(ExecuteSearchVideos);
        SearchMusicCommand = new AsyncRelayCommand(ExecuteSearchMusic);
        ClearLogCommand = new RelayCommand(() => LogOutput = "");

        DumpMemoryCommand = new RelayCommand(ExecuteDumpMemory);
        ForceGcCommand = new RelayCommand(ExecuteForceGc);
        ClearCachesCommand = new AsyncRelayCommand(ExecuteClearCaches);

        // ПРИМЕЧАНИЕ: CheckVmLeaksCommand переведен в no-op, так как фабрика бесстейтовая.
        CheckVmLeaksCommand = new RelayCommand(() =>
        {
            AppendLog("\n[Debug] Track VM cache monitoring disabled (Factory is stateless). VM leaks are impossible.");
        });

        PlayYoutubeAudioCommand = new AsyncRelayCommand(ExecutePlayYoutubeAudio);
        PlayYoutubeWithCacheCommand = new AsyncRelayCommand(ExecutePlayYoutubeWithCache);
        StopAudioTestCommand = new RelayCommand(ExecuteStopAudioTest);
        ShowCacheStatsCommand = new RelayCommand(ExecuteShowCacheStats);
        ClearAudioCacheCommand = new AsyncRelayCommand(ExecuteClearAudioCache);
        TestLocalFileCommand = new AsyncRelayCommand(ExecuteTestLocalFile);
        IsWatchdogEnabled = UIHangWatchdog.IsEnabled;
        WatchdogStatusText = IsWatchdogEnabled ? "Watchdog: ON" : "Watchdog: OFF";

        ToggleWatchdogCommand = new RelayCommand(() =>
        {
            var newState = !IsWatchdogEnabled;
            UIHangWatchdog.SetEnabled(newState);

            if (newState)
            {
                _uiWatchdog?.Dispose();
                _uiWatchdog = new UIHangWatchdog();
                _uiWatchdog.Start();
                AppendLog("[Watchdog] UI Hang Watchdog ENABLED (500ms threshold)");
            }
            else
            {
                _uiWatchdog?.Dispose();
                _uiWatchdog = null;
                AppendLog("[Watchdog] UI Hang Watchdog DISABLED");
            }

            IsWatchdogEnabled = newState;
            WatchdogStatusText = newState ? "Watchdog: ON" : "Watchdog: OFF";
        });
    }

    // Audio methods

    private async Task ExecutePlayYoutubeAudio() =>
        await PlayAudioTestAsync(useCache: false);

    private async Task ExecutePlayYoutubeWithCache() =>
        await PlayAudioTestAsync(useCache: true);

    private async Task PlayAudioTestAsync(bool useCache)
    {
        if (IsAudioPlaying)
        {
            AppendLog("⚠️ Audio test already running. Stop it first.");
            return;
        }

        IsBusy = true;
        IsAudioPlaying = true;
        _audioTestCts = new CancellationTokenSource();

        var cacheMode = useCache ? "WITH CACHE" : "NO CACHE";
        AppendLog($"\n╔════════════════════════════════════════╗");
        AppendLog($"║  🎵 AUDIO TEST ({cacheMode})");
        AppendLog($"╚════════════════════════════════════════╝");
        AppendLog($"  Input: {AudioTestInput}");
        AppendLog($"  Duration: {AudioTestDuration}s");

        try
        {
            var videoId = ExtractVideoId(AudioTestInput);
            if (string.IsNullOrEmpty(videoId))
            {
                AppendLog($"  ❌ Invalid YouTube URL/ID");
                return;
            }
            AppendLog($"  Video ID: {videoId}");

            AppendLog($"  → Getting stream URL...");
            var track = new TrackInfo
            {
                Id = videoId,
                Title = "Test Track",
                Author = "Unknown",
                Url = $"https://www.youtube.com/watch?v={videoId}"
            };

            try
            {
                var fullTrack = await Youtube.GetTrackByUrlAsync(track.Url);
                if (fullTrack != null) track = fullTrack;
                AppendLog($"  ✓ Title: {track.Title}");
                AppendLog($"  ✓ Author: {track.Author}");
            }
            catch (Exception ex)
            {
                AppendLog($"  ⚠️ Track info error: {ex.Message}");
            }

            var resolved = await Youtube.RefreshStreamAsync(
                track,
                forceRefresh: true,
                ct: _audioTestCts.Token);

            if (resolved == null)
            {
                AppendLog($"  ❌ Failed to get stream URL");
                return;
            }

            var descriptor = resolved.Value;

            AppendLog($"  ✓ Codec: {descriptor.Codec}, Bitrate: {descriptor.BitrateKbps}kbps");
            AppendLog($"  ✓ Container: {descriptor.Format}, Size: {descriptor.ContentLengthBytes / 1024.0 / 1024.0:F1}MB");
            AppendLog($"  ✓ HLS: {descriptor.Format == AudioFormat.Hls}");

            AppendLog($"  → Creating AudioPlayer...");

            if (useCache)
            {
                _testCacheManager = new AudioCacheManager();
                AudioSourceFactory.InitializeGlobalCache(_testCacheManager);
                AppendLog($"  ✓ Cache enabled");
            }

            var options = new AudioPlayerOptions
            {
                UrlRefreshCallback = async (_, ct) =>
                {
                    var refreshed = await Youtube.RefreshStreamAsync(track, forceRefresh: true, ct: ct);
                    return refreshed?.Url;
                }
            };

            _testPlayer = new AudioPlayer(options);

            AppendLog($"  → Starting playback...");
            await _testPlayer.PlayAsync(descriptor, ct: _audioTestCts.Token);

            AppendLog($"  ▶️ Playing for {AudioTestDuration}s...");

            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < AudioTestDuration &&
                   !_audioTestCts.Token.IsCancellationRequested &&
                   _testPlayer.State == PlaybackState.Playing)
            {
                await Task.Delay(1000, _audioTestCts.Token);
                var pos = _testPlayer.Position.TotalSeconds;
                var dur = _testPlayer.Duration.TotalSeconds;
                var buf = _testPlayer.BufferProgress;
                var downloaded = _testPlayer.GetDownloadedBytes() / 1024.0;
                AppendLog($"  ⏱️ {pos:F1}s / {dur:F1}s | Buffer: {buf:F0}% | Downloaded: {downloaded:F0}KB");
            }

            AppendLog($"  ✓ Test completed");

            if (_testCacheManager != null)
            {
                var stats = _testCacheManager.GetStats();
                AppendLog($"  📦 Cache: {stats.CompleteEntries} complete, {stats.TotalSizeFormatted}");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"  ⏹️ Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"  ❌ Error: {ex.Message}");
            AppendLog($"  Stack: {ex.StackTrace}");
        }
        finally
        {
            await CleanupAudioTest();
            IsBusy = false;
            IsAudioPlaying = false;
            AppendLog($"═══════════════════════════════════════\n");
        }
    }

    private void ExecuteStopAudioTest()
    {
        if (!IsAudioPlaying)
        {
            AppendLog("No audio test running.");
            return;
        }

        AppendLog("⏹️ Stopping audio test...");
        _audioTestCts?.Cancel();
        _ = CleanupAudioTest();
    }

    private async Task CleanupAudioTest()
    {
        if (_testPlayer != null)
        {
            await _testPlayer.DisposeAsync();
            _testPlayer = null;
        }

        if (_testCacheManager != null)
        {
            await _testCacheManager.DisposeAsync();
            _testCacheManager = null;
        }

        _audioTestCts?.Dispose();
        _audioTestCts = null;
        IsAudioPlaying = false;
    }

    private void ExecuteShowCacheStats()
    {
        AppendLog("\n--- AUDIO CACHE STATS ---");

        try
        {
            if (_testCacheManager != null)
            {
                var stats = _testCacheManager.GetStats();
                AppendLog($"  [Active Test Cache]");
                AppendLog($"  Total entries: {stats.TotalEntries}");
                AppendLog($"  Complete: {stats.CompleteEntries}");
                AppendLog($"  Partial: {stats.PartialEntries}");
                AppendLog($"  Size: {stats.TotalSizeFormatted} / {stats.MaxSizeFormatted}");
                AppendLog($"  Usage: {stats.UsagePercent:F1}%");
            }
            else
            {
                using var cacheManager = new AudioCacheManager();
                var stats = cacheManager.GetStats();
                AppendLog($"  [Disk Cache]");
                AppendLog($"  Total entries: {stats.TotalEntries}");
                AppendLog($"  Complete: {stats.CompleteEntries}");
                AppendLog($"  Partial: {stats.PartialEntries}");
                AppendLog($"  Size: {stats.TotalSizeFormatted} / {stats.MaxSizeFormatted}");
                AppendLog($"  Usage: {stats.UsagePercent:F1}%");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  Error: {ex.Message}");
        }

        AppendLog("--- END ---\n");
    }

    private async Task ExecuteClearAudioCache()
    {
        AppendLog("\n--- CLEARING AUDIO CACHE ---");
        IsBusy = true;

        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LMP", "AudioCache");

            if (Directory.Exists(cacheDir))
            {
                var files = Directory.GetFiles(cacheDir);
                foreach (var file in files)
                {
                    try { File.Delete(file); }
                    catch { /* ignored */ }
                }
                AppendLog($"  ✓ Deleted {files.Length} files");
            }
            else
            {
                AppendLog($"  Cache directory doesn't exist");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            AppendLog("--- CACHE CLEARED ---\n");
        }
    }

    private async Task ExecuteTestLocalFile()
    {
        AppendLog("\n--- LOCAL FILE TEST ---");
        AppendLog("  Select a .webm, .mp4, .m4a, or .ogg file to test.");

        var testPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "test.webm");

        if (!File.Exists(testPath))
        {
            AppendLog($"  ⚠️ Test file not found: {testPath}");
            AppendLog($"  Place a test audio file at this path.");
            return;
        }

        IsBusy = true;
        _audioTestCts = new CancellationTokenSource();
        IsAudioPlaying = true;

        try
        {
            AppendLog($"  File: {testPath}");

            var source = new LocalFileSource(testPath);
            if (!await source.InitializeAsync(_audioTestCts.Token))
            {
                AppendLog($"  ❌ Failed to initialize source");
                return;
            }

            AppendLog($"  ✓ Duration: {source.DurationMs}ms");
            AppendLog($"  ✓ Codec: {source.Codec}");
            AppendLog($"  ✓ Sample rate: {source.SampleRate}Hz");
            AppendLog($"  ✓ Channels: {source.Channels}");

            IAudioDecoder decoder = source.Codec == AudioCodec.Opus
                ? new OpusDecoder(source.SampleRate > 0 ? source.SampleRate : 48000,
                    source.Channels > 0 ? source.Channels : 2)
                : new AacDecoder(source.SampleRate > 0 ? source.SampleRate : 44100,
                    source.Channels > 0 ? source.Channels : 2);

            if (decoder is AacDecoder aac && source.DecoderConfig != null)
                aac.Initialize(source.DecoderConfig);

            IPlaybackBackend backend;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    backend = new NAudioBackend();
                    AppendLog("  ✓ NAudioBackend");
                }
                else
                {
                    backend = new NullAudioBackend();
                    AppendLog("  ⚠️ NullBackend (no audio output on non-Windows platforms)");
                }
            }
            catch
            {
                backend = new NullAudioBackend();
                AppendLog("  ⚠️ NullBackend (no audio output)");
            }

            var pcmBuffer = new LockFreeRingBuffer<float>(decoder.SampleRate * decoder.Channels * 4);
            var decodeOutput = new float[decoder.MaxFrameSize * decoder.Channels];

            backend.Initialize(decoder.SampleRate, decoder.Channels, buffer =>
            {
                int read = pcmBuffer.Read(buffer);
                if (read < buffer.Length) buffer[read..].Clear();
                return read / decoder.Channels;
            });

            var decodeTask = Task.Run(async () =>
            {
                try
                {
                    while (!_audioTestCts.Token.IsCancellationRequested)
                    {
                        while (pcmBuffer.Available < decodeOutput.Length)
                            await Task.Delay(5, _audioTestCts.Token);

                        var frame = await source.ReadFrameAsync(_audioTestCts.Token);
                        if (frame == null) break;

                        int samples = decoder.Decode(frame.Value.Data.Span, decodeOutput);
                        if (samples > 0)
                            pcmBuffer.Write(decodeOutput.AsSpan(0, samples * decoder.Channels));
                    }
                }
                catch (OperationCanceledException) { }
            });

            await Task.Delay(500, _audioTestCts.Token);

            backend.Start();
            AppendLog($"  ▶️ Playing for {AudioTestDuration}s...");

            var start = DateTime.Now;
            while ((DateTime.Now - start).TotalSeconds < AudioTestDuration &&
                   !_audioTestCts.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, _audioTestCts.Token);
                AppendLog($"  ⏱️ {source.PositionMs / 1000.0:F1}s");
            }

            backend.Stop();
            _audioTestCts.Cancel();

            backend.Dispose();
            decoder.Dispose();
            await source.DisposeAsync();

            AppendLog($"  ✓ Test completed");
        }
        catch (Exception ex)
        {
            AppendLog($"  ❌ Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsAudioPlaying = false;
            AppendLog("--- END ---\n");
        }
    }

    private static string? ExtractVideoId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        if (Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{11}$"))
            return input;

        var match = Regex.Match(input, @"[?&]v=([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(input, @"youtu\.be/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(input, @"embed/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(input, @"shorts/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    // Memory methods

    private void ExecuteDumpMemory()
    {
        AppendLog("\n╔══════════════════════════════════════════╗");
        AppendLog("║         MEMORY REPORT                    ║");
        AppendLog("╠══════════════════════════════════════════╣");

        var gcInfo = GC.GetGCMemoryInfo();
        AppendLog($"║ GC Total:       {GC.GetTotalMemory(false) / 1024 / 1024,6} MB              ║");
        AppendLog($"║ GC Heap:        {gcInfo.HeapSizeBytes / 1024 / 1024,6} MB              ║");
        AppendLog($"║ Memory Load:    {gcInfo.MemoryLoadBytes / 1024 / 1024,6} MB              ║");
        AppendLog($"║ High Threshold: {gcInfo.HighMemoryLoadThresholdBytes / 1024 / 1024,6} MB              ║");
        AppendLog($"║ Gen0/1/2: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2),-6}                   ║");
        AppendLog("╚══════════════════════════════════════════╝\n");
    }

    private void ExecuteForceGc()
    {
        AppendLog("\n--- FORCING GARBAGE COLLECTION ---");
        var before = GC.GetTotalMemory(false) / 1024 / 1024;
        AppendLog($"Before: {before} MB");

        // Используем централизованный хелпер
        MemoryCleanupHelper.PerformCleanup(aggressive: true);

        // Ждём завершения (синхронно для Debug)
        Thread.Sleep(500);
        var after = GC.GetTotalMemory(true) / 1024 / 1024;
        AppendLog($"After:  {after} MB");
        AppendLog($"Freed:  {before - after} MB");
        AppendLog("--- GC COMPLETE ---\n");
    }

    private async Task ExecuteClearCaches()
    {
        AppendLog("\n--- CLEARING ALL CACHES ---");
        IsBusy = true;

        try
        {
            var imageCache = AppEntry.Services.GetRequiredService<ImageCacheService>();
            imageCache.ClearMemoryCache();
            AppendLog("✓ Image memory cache cleared");

            var searchCache = AppEntry.Services.GetRequiredService<SearchCacheService>();
            searchCache.ClearAll();
            AppendLog("✓ Search cache cleared");

            Youtube.ClearCache();
            AppendLog("✓ YouTube stream URL cache cleared");

            MemoryCleanupHelper.PerformCleanup(aggressive: true);
            AppendLog("✓ GC + Skia caches completed");

            await Task.Delay(300);
            var memMb = GC.GetTotalMemory(false) / 1024 / 1024;
            AppendLog($"\nCurrent GC memory: {memMb} MB");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            AppendLog("--- CACHES CLEARED ---\n");
        }
    }

    // YouTube methods

    private async Task ExecuteGetLikedVideos()
    {
        await RunSafe("YT LIKED (LL)", async () =>
        {
            var videos = await Youtube.GetClient().Playlists
                .GetVideosAsync(new Core.Youtube.Playlists.PlaylistId("LL"))
                .Take(10)
                .ToListAsync();
            return videos;
        });
    }

    private async Task ExecuteGetLikedMusic()
    {
        await RunSafe("YTM LIKED (VLLM)", async () =>
        {
            var tracks = await Youtube.GetClient().Music.GetLikedTracksAsync();
            return [.. tracks.Take(10)];
        });
    }

    private async Task ExecuteSearchVideos()
    {
        await RunSafe($"YT SEARCH: {SearchQuery}", async () =>
            await Youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Video));
    }

    private async Task ExecuteSearchMusic()
    {
        await RunSafe($"YTM SEARCH: {SearchQuery}", async () =>
            await Youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Music));
    }

    private async Task RunSafe(string title, Func<Task<List<TrackInfo>>> action)
    {
        IsBusy = true;
        AppendLog($"\n--- STARTING: {title} ---");
        try
        {
            var results = await action();
            AppendLog($"Success! Found {results.Count} items:");
            foreach (var item in results)
                AppendLog($"- [{item.Id}] {item.Title} by {item.Author} (Music: {item.IsMusic})");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            if (ex.InnerException != null) AppendLog($"INNER: {ex.InnerException.Message}");
        }
        finally
        {
            AppendLog($"--- FINISHED: {title} ---\n");
            IsBusy = false;
        }
    }

    // LOG

    private void AppendLog(string text) =>
        LogOutput += text + "\n";

    // DISPOSE

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audioTestCts?.Cancel();
            _audioTestCts?.Dispose();
            _testPlayer?.Dispose();
            _testCacheManager?.Dispose();
            _uiWatchdog?.Dispose();
            _uiWatchdog = null;
            TestRunner.Dispose();
        }
        base.Dispose(disposing);
    }
}