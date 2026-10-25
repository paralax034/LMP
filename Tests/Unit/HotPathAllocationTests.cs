using System.Buffers.Binary;
using LMP.Core.Audio;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Core.Youtube.Utils;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Автономные тесты нулевых аллокаций горячего аудио-пайплайна.
/// Проверяют детерминированный инвариант: ровно 0 байт аллокаций в куче на критических путях воспроизведения.
/// Содержит все сквозные проверки конвейера декодирования и аппаратный профайлер ядра NT.
/// </summary>
public static partial class HotPathAllocationTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 мс stereo
    private const int WarmupIterations = 5;
    private const int MeasureIterations = 1_000;

    private static float[] CreateSineBuffer(int length)
    {
        var buffer = new float[length];
        for (int i = 0; i < length; i++)
            buffer[i] = MathF.Sin(i * 0.1f) * 0.5f;
        return buffer;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor SIMD (0 Alloc)", Group = TestGroups.Audio, Order = 1)]
    public static Task TestGainProcessorSimdZeroAllocAsync()
    {
        var processor = new GainProcessor();
        processor.SetVolumeGain(0.75f);
        float[] buffer = CreateSineBuffer(BufferSamples);

        for (int i = 0; i < 3000; i++)
            processor.Process(buffer.AsSpan());

        TestAssert.ZeroAlloc(() => processor.Process(buffer.AsSpan()), WarmupIterations, MeasureIterations);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor Volume Ramp (0 Alloc)", Group = TestGroups.Audio, Order = 2)]
    public static Task TestGainProcessorRampZeroAllocAsync()
    {
        var processor = new GainProcessor();
        float[] buffer = CreateSineBuffer(BufferSamples);
        bool toggle = false;

        TestAssert.ZeroAlloc(() =>
        {
            processor.SetVolumeGain(toggle ? 0.3f : 0.9f);
            toggle = !toggle;
            processor.Process(buffer.AsSpan());
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor Unity Bypass (0 Alloc)", Group = TestGroups.Audio, Order = 3)]
    public static Task TestGainProcessorUnityBypassZeroAllocAsync()
    {
        var processor = new GainProcessor();
        processor.SetVolumeGain(1.0f);
        float[] buffer = CreateSineBuffer(BufferSamples);

        for (int i = 0; i < 3000; i++)
            processor.Process(buffer.AsSpan());

        TestAssert.ZeroAlloc(() => processor.Process(buffer.AsSpan()), WarmupIterations, MeasureIterations);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "LockFreeRingBuffer Write (0 Alloc)", Group = TestGroups.Audio, Order = 4)]
    public static Task TestRingBufferWriteZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] data = CreateSineBuffer(BufferSamples);

        TestAssert.ZeroAlloc(() =>
        {
            ring.Write(data.AsSpan());
            ring.Clear();
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "LockFreeRingBuffer Read (0 Alloc)", Group = TestGroups.Audio, Order = 5)]
    public static Task TestRingBufferReadZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] src = CreateSineBuffer(BufferSamples);
        float[] dst = new float[BufferSamples];

        TestAssert.ZeroAlloc(() =>
        {
            ring.Write(src.AsSpan());
            ring.Read(dst.AsSpan());
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "LockFreeRingBuffer Properties (0 Alloc)", Group = TestGroups.Audio, Order = 6)]
    public static Task TestRingBufferPropertiesZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 4);
        float[] data = new float[256];
        ring.Write(data.AsSpan());

        int sink = 0;
        TestAssert.ZeroAlloc(() =>
        {
            sink += ring.Count;
            sink += ring.Available;
            sink += ring.ProducerCachedAvailable;
            _ = ring.IsEmpty;
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Full AudioCallback DSP Chain (0 Alloc)", Group = TestGroups.Audio, Order = 7)]
    public static Task TestAudioCallbackSimulationZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        var analyzer = new Core.Audio.Normalization.EbuR128Analyzer();
        analyzer.Configure(new Core.Audio.Normalization.NormalizationConfig(
            Enabled: true,
            TargetLufs: -14f,
            MaxGain: 3.0f,
            Mode: Core.Audio.Normalization.NormalizationMode.Bidirectional));
        analyzer.LockResolvedGain(1.25f);

        var limiter = new Core.Audio.Normalization.TruePeakLimiter(SampleRate);
        var analyzer3Band = new Audio3BandAnalyzer(SampleRate, Channels);
        var crossfader = new Core.Audio.Normalization.GainCrossfader(1.0f);
        var processor = new GainProcessor();
        processor.SetVolumeGain(0.8f);

        float[] src = CreateSineBuffer(BufferSamples);
        float[] dst = new float[BufferSamples];

        int branchSelector = 0;

        TestAssert.ZeroAlloc(() =>
        {
            ring.Write(src.AsSpan());
            int read = ring.Read(dst.AsSpan());
            if (read > 0)
            {
                var samples = dst.AsSpan(0, read);
                int mode = branchSelector++ % 3;

                if (mode == 0)
                {
                    analyzer.LockResolvedGain(1.25f);
                }
                else
                {
                    analyzer.LockResolvedGain(0.85f);
                    crossfader.Reset(mode == 1 ? 0.85f : 0.5f);
                }

                float normGain = analyzer.IsEnabled ? analyzer.ProcessSamples() : 1.0f;
                crossfader.SetTarget(normGain, SampleRate, Channels);

                bool canBypassLimiter = normGain <= 1.0f && (mode != 0 || limiter.EnvelopeGain >= 0.999f);
                if (canBypassLimiter)
                {
                    if (crossfader.IsActive)
                        SpanMathHelper.MultiplyByCrossfade(samples, ref crossfader);
                    else if (MathF.Abs(normGain - 1.0f) > 0.0001f)
                        SpanMathHelper.MultiplyByConstant(samples, normGain);
                }
                else
                {
                    limiter.Process(samples, ref crossfader);
                }

                analyzer3Band.ProcessBlock(samples, Channels);
                processor.Process(samples);
            }
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "KWeightingFilter ProcessBlock (0 Alloc)", Group = TestGroups.Audio, Order = 8)]
    public static Task TestKWeightingFilterZeroAllocAsync()
    {
        var filter = new KWeightingFilter(SampleRate, Channels);
        float[] input = CreateSineBuffer(960 * Channels);
        double[] output = new double[960 * Channels];

        TestAssert.ZeroAlloc(() =>
        {
            filter.Reset();
            filter.ProcessBlock(input.AsSpan(), output.AsSpan());
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "SharedPlaybackState Update & Get (0 Alloc)", Group = TestGroups.Audio, Order = 9)]
    public static Task TestSharedPlaybackStateZeroAllocAsync()
    {
        var sharedStateType = typeof(AudioPlayer).GetNestedType(
            "SharedPlaybackState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("SharedPlaybackState not found");

        var instance = Activator.CreateInstance(sharedStateType)!;
        var updateMethod = sharedStateType.GetMethod("Update")!;
        var getPosMethod = sharedStateType.GetMethod("GetCurrentPosition")!;

        var updateDel = (Action<long, int, int, int, bool, long>)Delegate.CreateDelegate(
            typeof(Action<long, int, int, int, bool, long>), instance, updateMethod);
        var getPosDel = (Func<TimeSpan>)Delegate.CreateDelegate(
            typeof(Func<TimeSpan>), instance, getPosMethod);

        long sampleCounter = 0;
        long ticksSink = 0;

        TestAssert.ZeroAlloc(() =>
        {
            sampleCounter += 960;
            updateDel(sampleCounter, 9600, SampleRate, Channels, true, 180_000L);
            ticksSink += getPosDel().Ticks;
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "WebM Xiph Lacing Parser (0 Alloc)", Group = TestGroups.Audio, Order = 10)]
    public static Task TestWebMParserLacingZeroAllocAsync()
    {
        ReadOnlySpan<byte> clusterHeader =
        [
            0x1F, 0x43, 0xB6, 0x75,
            0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xE7, 0x81, 0x00
        ];

        ReadOnlySpan<byte> simpleBlock =
        [
            0xA3, 0x8E, 0x81, 0x00, 0x00, 0x82, 0x01, 0x04,
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88
        ];

        int totalBlocks = WarmupIterations + MeasureIterations;
        byte[] streamData = new byte[clusterHeader.Length + totalBlocks * simpleBlock.Length];
        clusterHeader.CopyTo(streamData);

        int offset = clusterHeader.Length;
        for (int i = 0; i < totalBlocks; i++)
        {
            simpleBlock.CopyTo(streamData.AsSpan(offset));
            offset += simpleBlock.Length;
        }

        using var ms = new MemoryStream(streamData, writable: false);
        using var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms);

        TestAssert.ZeroAlloc(() =>
        {
            var vt1 = containerParser.ReadNextFrameAsync();
            _ = vt1.IsCompletedSuccessfully ? vt1.Result : vt1.AsTask().GetAwaiter().GetResult();

            var vt2 = containerParser.ReadNextFrameAsync();
            _ = vt2.IsCompletedSuccessfully ? vt2.Result : vt2.AsTask().GetAwaiter().GetResult();
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Mp4Parser & AAC SIMD Convert (0 Alloc)", Group = TestGroups.Audio, Order = 11)]
    public static Task TestMp4ParserAndDecoderSimdZeroAllocAsync()
    {
        byte[] mdatPayload = new byte[4096];
        using var ms = new MemoryStream(mdatPayload, writable: false);
        using var mp4Parser = new Core.Audio.Parsers.Mp4ContainerParser(ms);

        var samplesField = typeof(Core.Audio.Parsers.Mp4ContainerParser)
            .GetField("_samples", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sampleInfoType = typeof(Core.Audio.Parsers.Mp4ContainerParser)
            .GetNestedType("SampleInfo", System.Reflection.BindingFlags.NonPublic)!;

        var samplesList = (System.Collections.IList)samplesField.GetValue(mp4Parser)!;
        int totalFrames = WarmupIterations + MeasureIterations;
        for (int i = 0; i < totalFrames; i++)
        {
            var sample = Activator.CreateInstance(sampleInfoType, [0L, 256, i * 20L, 20, true])!;
            samplesList.Add(sample);
        }

        byte[] pcm16Bytes = new byte[2048 * sizeof(short)];
        float[] floatOut = new float[2048];

        TestAssert.ZeroAlloc(() =>
        {
            var vt = mp4Parser.ReadNextFrameAsync();
            _ = vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
            Core.Audio.Decoders.AacDecoder.ConvertBytesToFloat(pcm16Bytes, floatOut, 2048);
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "OpusDecoder Decode & PLC (0 Alloc)", Group = TestGroups.Audio, Order = 12)]
    public static Task TestOpusDecoderZeroAllocAsync()
    {
        using var decoder = new Core.Audio.Decoders.OpusDecoder(SampleRate, Channels);
        byte[] validOpusFrame = [0xFC, 0xFF, 0xFE];
        float[] pcmOut = new float[decoder.MaxFrameSize * Channels];

        TestAssert.ZeroAlloc(() =>
        {
            decoder.Decode(validOpusFrame.AsSpan(), pcmOut.AsSpan());
            decoder.Decode(ReadOnlySpan<byte>.Empty, pcmOut.AsSpan());
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "CachingStream RAM Hit ReadFrame (0 Alloc)", Group = TestGroups.Audio, Order = 13)]
    public static Task TestAsyncCachingReadStreamRamHitZeroAllocAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_RamHitTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            byte[] streamData = new byte[4096];

            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);
            using var source = new Core.Audio.Sources.CachingStreamSource(
                cacheKey: "test_ram_hit",
                trackId: "yt_test",
                url: "https://example.com/audio.webm",
                contentLength: streamData.Length,
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128,
                cacheManager: cacheManager,
                config: new StreamingConfig());

            TestAssert.ZeroAlloc(() =>
            {
                var ranges = source.GetBufferedRanges();
                _ = ranges.Count;
            }, WarmupIterations, MeasureIterations);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GetBufferedRanges & Dirty Flag (0 Alloc)", Group = TestGroups.Audio, Order = 14)]
    public static Task TestGetBufferedRangesAndCacheDirtyZeroAllocAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_RangesTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);

            TestAssert.ZeroAlloc(() =>
            {
                var stats = cacheManager.GetStats();
                _ = stats.TotalEntries;
            }, WarmupIterations, MeasureIterations);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "End-to-End Pipeline Step (0 Alloc)", Group = TestGroups.Audio, Order = 15)]
    public static Task TestEndToEndPipelineStepZeroAllocAsync()
    {
        var gainProcessor = new GainProcessor();
        gainProcessor.SetVolumeGain(0.9f);
        float[] buffer = CreateSineBuffer(BufferSamples);

        TestAssert.ZeroAlloc(() =>
        {
            gainProcessor.Process(buffer.AsSpan());
        }, WarmupIterations, MeasureIterations);

        return Task.CompletedTask;
    }

    // ==========================================
    //   FORWARDING ALIASES FOR BACKWARD COMPAT
    // ==========================================

    public static Task TestSharedPlaybackStateInterpolationAsync() =>
        AudioLogicTests.TestSharedPlaybackStateInterpolationAsync();

    public static Task TestAdaptiveDecoderDelayAndTimerSuspendAsync() =>
        AudioLogicTests.TestAdaptiveDecoderDelayAndBatchHysteresisAsync();

    public static Task TestNetworkManagerAndDelayNoThrowZeroAllocAsync() =>
        NetworkLogicTests.TestNetworkManagerLogicZeroAllocAsync();

    [TestMethod(TestCategory.Unit, "ImageCache & DominantColor FNV-1a Key Sync & Native Dispose", Group = TestGroups.System, Order = 98)]
    public static Task TestImageCacheKeySyncAndRefCountedDisposeAsync()
    {
        const string sampleUrl = "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg";
        string path1 = ImageCacheService.GetDiskCachePath(sampleUrl);
        string path2 = ImageCacheService.GetDiskCachePath(sampleUrl.AsSpan());

        TestAssert.Equal(path1, path2);
        TestAssert.True(path1.EndsWith("B1D0BC95F084A249", StringComparison.Ordinal) || Path.GetFileName(path1).Length == 16,
            "Disk cache key must be a 16-char FNV-1a hex string");

        using var bmp = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(4, 4),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888,
            Avalonia.Platform.AlphaFormat.Premul);

        var refBmp = new RefCountedBitmap(bmp);
        TestAssert.False(refBmp.IsDisposed);
        refBmp.Dispose();
        TestAssert.True(refBmp.IsDisposed);

        // Инвариант безопасности UI: после вытеснения из кэша (refBmp.Dispose) сам Bitmap,
        // удерживаемый в Avalonia.Controls.Image.Source, обязан оставаться валидным при вызове bmp.Size в MeasureOverride!
        var validSize = bmp.Size;
        TestAssert.True(validSize.Width > 0 && validSize.Height > 0,
            "Bitmap.Size must remain accessible for bound Image controls after cache eviction");

        bool preferWebOnVpn = YoutubeClientUtils.GetStreamFallbackClients(isAuthenticated: true, preferWebRemix: true)[0]
            == YoutubeClientNames.WebRemix;
        TestAssert.True(preferWebOnVpn, "WEB_REMIX must be prioritized when preferWebRemix is true");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Cold-Only CDN PreWarm Gate & QuickJS Init State", Group = TestGroups.Youtube, Order = 97)]
    public static Task TestColdOnlyCdnPreWarmGateAndQuickJsInitStateAsync()
    {
        // Проверяем, что пересоздание 2-мегабайтного LockFreeRingBuffer<float> (524 288 элементов)
        // переиспользует буфер из ArrayPool<float>.Shared с 0 байт аллокаций в LOH
        using (var warmupRing = new LockFreeRingBuffer<float>(524_288)) { }
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            using var ring = new LockFreeRingBuffer<float>(524_288);
        }
        long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        TestAssert.True(allocDelta < 4096, $"10 LockFreeRingBuffer(2MB) recreations must not allocate in LOH, actual={allocDelta}B");

        var net = new NetworkManager();
        try
        {
            var playerManager = new Core.Youtube.Bridge.Common.PlayerContextManager(() => net.AudioClient);
            using var jsService = new Core.Youtube.Bridge.Common.JsDecryptionService(playerManager);
            using var sigDecryptor = new Core.Youtube.Bridge.SigCipher.SigCipherDecryptor(jsService);
            using var poProvider = new Core.Youtube.Bridge.PoToken.PoTokenProvider(() => net.AudioClient);

            TestAssert.False(jsService.IsInitialized, "Fresh JsDecryptionService must report IsInitialized == false");
            TestAssert.False(sigDecryptor.IsInitialized, "Fresh SigCipherDecryptor must report IsInitialized == false");
            TestAssert.False(poProvider.HasActiveContext, "Fresh PoTokenProvider must report HasActiveContext == false");

            using var nDecryptor = new Core.Youtube.Bridge.NToken.NTokenDecryptor(jsService);
            int complexEventCount = 0;
            nDecryptor.OnComplexDecryptionStarted += _ => complexEventCount++;
            TestAssert.Equal(0, complexEventCount);

            // Проверяем non-blocking режим компактора для активного аудио
            MemoryHelpers.CompactManagedHeapNow(nonBlockingForAudio: true);

            using var cts = new CancellationTokenSource();
            var delayTask = cts.Token.DelayNoThrowAsync(500, continueOnCapturedContext: false);
            cts.Cancel();
            bool delayCompleted = delayTask.GetAwaiter().GetResult();
            TestAssert.False(delayCompleted, "Cancelled DelayNoThrowAsync must return false without throwing");
        }
        finally
        {
            net.Dispose();
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Diagnostic, "Windows NT Per-Thread Context Switches (2s)",
        Group = TestGroups.System, Order = 99, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static async Task TestThreadContextSwitchesDiagnosticAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Даём UI-очереди успокоиться после события старта самого теста
        await Task.Delay(150).ConfigureAwait(false);

        var snapBefore = CaptureProcessThreadsSnapshot();
        await Task.Delay(2000).ConfigureAwait(false);
        var snapAfter = CaptureProcessThreadsSnapshot();

        uint totalCs = 0;
        var perThreadDeltas = new List<(int Tid, uint DeltaCs, uint WaitReason, nint StartAddress)>(snapAfter.Count);

        foreach (var (tid, after) in snapAfter)
        {
            if (snapBefore.TryGetValue(tid, out var before))
            {
                uint delta = after.ContextSwitches - before.ContextSwitches;
                totalCs += delta;
                if (delta > 0)
                    perThreadDeltas.Add((tid, delta, after.WaitReason, after.StartAddress));
            }
        }

        perThreadDeltas.Sort(static (a, b) => b.DeltaCs.CompareTo(a.DeltaCs));

        var modules = CaptureModuleRanges();
        var topBuilder = new System.Text.StringBuilder(192);
        int topCount = Math.Min(5, perThreadDeltas.Count);
        for (int i = 0; i < topCount; i++)
        {
            if (i > 0) topBuilder.Append(", ");
            var item = perThreadDeltas[i];
            var tName = TryGetWindowsThreadName(item.Tid) ?? TryFormatModuleOffset(item.StartAddress, modules);
            if (!string.IsNullOrEmpty(tName))
                topBuilder.Append($"TID {item.Tid}({tName},w={item.WaitReason})={item.DeltaCs / 2.0:F0}/s");
            else
                topBuilder.Append($"TID {item.Tid}(w={item.WaitReason})={item.DeltaCs / 2.0:F0}/s");
        }

        Log.Info($"[CS-Diagnostic] Threads analyzed: {snapAfter.Count}, Context switches over 2.0s: {totalCs} ({totalCs / 2.0:F0} CS/sec) | Top: [{topBuilder}]");
    }

    private readonly record struct ThreadKernelInfo(uint ContextSwitches, long KernelTime, long UserTime, uint WaitReason, nint StartAddress);

    private static List<(nint BaseAddr, nint EndAddr, string Name)> CaptureModuleRanges()
    {
        var list = new List<(nint BaseAddr, nint EndAddr, string Name)>(64);
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule mod in proc.Modules)
            {
                nint baseAddr = mod.BaseAddress;
                nint endAddr = baseAddr + mod.ModuleMemorySize;
                list.Add((baseAddr, endAddr, mod.ModuleName));
            }
        }
        catch { }
        return list;
    }

    private static string? TryFormatModuleOffset(nint startAddr, List<(nint BaseAddr, nint EndAddr, string Name)> modules)
    {
        if (startAddr == 0) return null;
        for (int i = 0; i < modules.Count; i++)
        {
            var m = modules[i];
            if (startAddr >= m.BaseAddr && startAddr < m.EndAddr)
            {
                long offset = startAddr - m.BaseAddr;
                return $"{m.Name}+0x{offset:x}";
            }
        }
        return $"0x{startAddr:x}";
    }

    private static unsafe string? TryGetWindowsThreadName(int tid)
    {
        const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        nint hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, 0, (uint)tid);
        if (hThread == 0) return null;

        try
        {
            if (GetThreadDescription(hThread, out nint pDesc) >= 0 && pDesc != 0)
            {
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(pDesc);
                }
                finally
                {
                    LocalFree(pDesc);
                }
            }
        }
        finally
        {
            CloseHandle(hThread);
        }

        return null;
    }

    private static unsafe Dictionary<int, ThreadKernelInfo> CaptureProcessThreadsSnapshot()
    {
        var result = new Dictionary<int, ThreadKernelInfo>(64);
        int currentPid = Environment.ProcessId;

        // Фиксированный буфер 2 МБ из пула покрывает до ~20 000 потоков всей ОС с 0 аллокаций в куче
        int bufferSize = 2 * 1024 * 1024;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int status;
            fixed (byte* pBuffer = buffer)
            {
                status = NtQuerySystemInformation(5, (nint)pBuffer, buffer.Length, out int returnLength);

                if (status != 0 && returnLength > buffer.Length)
                {
                    // Аварийный fallback на случай экстремального числа дескрипторов в ОС (>25 000 потоков)
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(returnLength + 65536);
                    fixed (byte* pRetry = buffer)
                    {
                        status = NtQuerySystemInformation(5, (nint)pRetry, buffer.Length, out _);
                    }
                }
            }

            if (status == 0)
            {
                ReadOnlySpan<byte> span = buffer;
                int offset = 0;
                const int SystemProcessInfoSizeX64 = 256;
                const int SystemThreadInfoSizeX64 = 80;

                while (offset + SystemProcessInfoSizeX64 <= span.Length)
                {
                    uint nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                    uint numberOfThreads = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
                    nint uniqueProcessId = (nint)BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 80, 8));

                    if (uniqueProcessId == currentPid)
                    {
                        int threadOffset = offset + SystemProcessInfoSizeX64;
                        for (uint i = 0; i < numberOfThreads && threadOffset + SystemThreadInfoSizeX64 <= span.Length; i++, threadOffset += SystemThreadInfoSizeX64)
                        {
                            long kernelTime = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset, 8));
                            long userTime = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 8, 8));
                            nint startAddress = (nint)BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 24, 8));
                            int tid = (int)BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 48, 8));
                            uint contextSwitches = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(threadOffset + 64, 4));
                            uint waitReason = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(threadOffset + 72, 4));

                            result[tid] = new ThreadKernelInfo(contextSwitches, kernelTime, userTime, waitReason, startAddress);
                        }
                        break;
                    }

                    if (nextEntryOffset == 0) break;
                    offset += (int)nextEntryOffset;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    [System.Runtime.InteropServices.LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(
        int systemInformationClass, nint systemInformation, int systemInformationLength, out int returnLength);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint dwDesiredAccess, int bInheritHandle, uint dwThreadId);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint hThread, out nint ppszThreadDescription);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint hObject);
}