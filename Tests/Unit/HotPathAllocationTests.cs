using System.Runtime.CompilerServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;
using LMP.Core.Youtube.Utils;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Zero-allocation offline unit tests for the audio hot-path pipeline and cache key helpers.
/// Validates the deterministic invariant: exactly 0 bytes allocated on the heap during critical playback paths.
/// </summary>
public static class HotPathAllocationTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int CallbackSamples = 1_024 * Channels; // ~21.3 ms stereo WASAPI block
    private const int WarmupIterations = 5;
    private const int MeasureIterations = 1_000;

    /// <summary>
    /// Standard assembly of the Mp4ContainerParser sample table without reflection (AOT): a private list of SampleInfo
    /// is populated by the same code used when parsing stbl of a real file.
    /// </summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BuildSampleTable")]
    private static extern void BuildMp4SampleTable(
        Core.Audio.Parsers.Mp4ContainerParser parser,
        List<uint> sizes,
        List<(uint firstChunk, uint samplesPerChunk, uint descIdx)> stsc,
        List<long> offsets,
        List<(uint count, uint delta)> stts,
        uint timeScale);

    private static float[] CreateSineBuffer(int length)
    {
        var buffer = new float[length];
        for (int i = 0; i < length; i++)
            buffer[i] = MathF.Sin(i * 0.1f) * 0.5f;
        return buffer;
    }

    [TestMethod(TestCategory.Unit, "Real AudioPipeline.AudioCallback & RingBuffer LOH Reuse (0 Alloc)", Group = TestGroups.Audio, Order = 7)]
    public static async Task TestAudioPipelineRealCallbackZeroAllocAsync()
    {
        // 1. Real AudioPipeline.AudioCallback hot path with EQ + EBU R128 + TruePeakLimiter + Spectrum active
        using var backend = new NullAudioBackend();
        var ring = new LockFreeRingBuffer<float>(CallbackSamples * 8);
        var pipeline = AudioPipelineTestHarness.Create(
            new BlockingAudioSource(SampleRate, Channels),
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0.25f),
            backend,
            ring);

        try
        {
            pipeline.Equalizer.Configure(
                enabled: true,
                EqualizerPresets.GetPresetBands(EqualizerPresets.BassBoostId),
                preampDb: 0f,
                autoPreamp: true);
            pipeline.Equalizer.SnapToTarget();

            pipeline.Analyzer.Configure(new NormalizationConfig(
                Enabled: true,
                TargetLufs: -14f,
                MaxGain: 3.0f,
                Mode: NormalizationMode.Bidirectional));
            pipeline.Analyzer.LockResolvedGain(1.25f);
            pipeline.SnapCrossfaderToGain();

            float[] src = CreateSineBuffer(CallbackSamples);
            float[] dst = new float[CallbackSamples];
            int expectedFrames = CallbackSamples / Channels;

            // Activate visualizer reader lease before warmup
            _ = AudioVisualizerBuffer.OverallLevel;

            int iteration = 0;
            TestAssert.ZeroAlloc(() =>
            {
                if ((iteration++ & 3) == 0)
                {
                    // Refresh reader lease and alternate normalization target to keep spectrum and crossfader active
                    _ = AudioVisualizerBuffer.OverallLevel;
                    pipeline.Analyzer.LockResolvedGain((iteration & 4) == 0 ? 1.25f : 0.85f);
                }

                ring.Write(src.AsSpan());
                int frames = AudioPipelineTestHarness.AudioCallback(pipeline, dst.AsSpan());
                if (frames != expectedFrames)
                    TestAssert.Fail($"Expected {expectedFrames} rendered frames, got {frames}");
            }, WarmupIterations, MeasureIterations, context: "AudioPipeline.AudioCallback");

            TestAssert.True(
                AudioVisualizerBuffer.HasRecentActivity,
                "AudioSpectrumAnalyzer must remain active inside AudioCallback throughout the zero-alloc run");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            AudioVisualizerBuffer.Reset();
        }

        // 2. Recreating a 2 MB LockFreeRingBuffer<float> reuses ArrayPool<float>.Shared without LOH allocations
        using (var warmupRing = new LockFreeRingBuffer<float>(524_288))
        {
            _ = warmupRing.Capacity;
        }

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            using var pooledRing = new LockFreeRingBuffer<float>(524_288);
            _ = pooledRing.Capacity;
        }
        long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        TestAssert.True(allocDelta < 4096, $"10 LockFreeRingBuffer(2MB) recreations must not allocate in LOH, actual={allocDelta}B");
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

        int totalFrames = WarmupIterations + MeasureIterations;
        var sizes = new List<uint>(totalFrames);
        var chunkOffsets = new List<long>(totalFrames);
        for (int i = 0; i < totalFrames; i++)
        {
            sizes.Add(256);
            chunkOffsets.Add(0);
        }

        BuildMp4SampleTable(mp4Parser, sizes, [(1u, 1u, 1u)], chunkOffsets, [((uint)totalFrames, 20u)], timeScale: 1000);

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

    /// <summary>
    /// Validates consistency of the FNV-1a disk cache key for string/span overloads, WEB_REMIX client priority,
    /// and deterministic disposal of <see cref="RefCountedBitmap"/>. Bitmap checks require an initialized
    /// Avalonia platform, so they are skipped in headless CLI.
    /// </summary>
    [TestMethod(TestCategory.Unit, "ImageCache & DominantColor FNV-1a Key Sync & Native Dispose", Group = TestGroups.System, Order = 98)]
    public static Task TestImageCacheKeySyncAndRefCountedDisposeAsync()
    {
        const string sampleUrl = "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg";
        string path1 = ImageCacheService.GetDiskCachePath(sampleUrl);
        string path2 = ImageCacheService.GetDiskCachePath(sampleUrl.AsSpan());

        TestAssert.Equal(path1, path2);
        TestAssert.True(path1.EndsWith("B1D0BC95F084A249", StringComparison.Ordinal) || Path.GetFileName(path1).Length == 16,
            "Disk cache key must be a 16-char FNV-1a hex string");

        bool preferWebOnVpn = YoutubeClientUtils.GetStreamFallbackClients(isAuthenticated: true, preferWebRemix: true)[0]
            == YoutubeClientNames.WebRemix;
        TestAssert.True(preferWebOnVpn, "WEB_REMIX must be prioritized when preferWebRemix is true");

        if (Avalonia.Application.Current is null)
            TestAssert.Skip("Avalonia platform is not initialized (headless CLI); bitmap dispose checks require UI test mode");

        using var bmp = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(4, 4),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888,
            Avalonia.Platform.AlphaFormat.Premul);

        var refBmp = new RefCountedBitmap(bmp);
        TestAssert.False(refBmp.IsDisposed);
        refBmp.Dispose();
        TestAssert.True(refBmp.IsDisposed);

        var validSize = bmp.Size;
        TestAssert.True(validSize.Width > 0 && validSize.Height > 0,
            "Bitmap.Size must remain accessible for bound Image controls after cache eviction");

        return Task.CompletedTask;
    }
}