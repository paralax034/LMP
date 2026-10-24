using LMP.Core.Audio;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Тесты нулевых аллокаций горячего пути аудио-пайплайна.
/// </summary>
/// <remarks>
/// <para>
/// Использует <see cref="GC.GetAllocatedBytesForCurrentThread"/> для точного измерения
/// аллокаций на вызывающем потоке без влияния фонового GC.
/// </para>
/// <para>
/// Каждый тест выполняет прогревочные вызовы (JIT-компиляция, заполнение кэшей),
/// затем измеряет N горячих итераций и утверждает ноль аллокаций.
/// </para>
/// <para>
/// Регрессия: любое ненулевое значение означает скрытый boxing, замыкание
/// или непреднамеренный <c>new</c> в hot path.
/// </para>
/// </remarks>
public static partial class HotPathAllocationTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    /// <summary>100 мс при 48 kHz stereo = 9600 float.</summary>
    private const int BufferSamples = SampleRate / 10 * Channels;
    private const int WarmupIterations = 5;
    private const int MeasureIterations = 2_000;

    // Вспомогательные функции

    /// <summary>
    /// Измеряет аллокации управляемой кучи вызывающего потока за <paramref name="iterations"/>
    /// вызовов <paramref name="action"/>, предварительно выполнив прогрев.
    /// </summary>
    /// <param name="action">Делегат горячего пути. Не должен аллоцировать.</param>
    /// <param name="warmup">Количество прогревочных итераций (JIT, кэши).</param>
    /// <param name="iterations">Количество измеряемых итераций.</param>
    /// <returns>Суммарные аллокации в байтах за все измеряемые итерации.</returns>
    private static long MeasureAllocations(
        Action action,
        int warmup = WarmupIterations,
        int iterations = MeasureIterations)
    {
        for (int i = 0; i < warmup; i++) action();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        long after = GC.GetAllocatedBytesForCurrentThread();

        return after - before;
    }

    private static void AssertZeroAlloc(long allocated, string context)
    {
        if (allocated != 0)
            throw new InvalidOperationException(
                $"[Assertion Failed] {context}: expected 0 bytes allocated, got {allocated} bytes " +
                $"over {MeasureIterations} iterations ({(double)allocated / MeasureIterations:F2} B/call)");
    }

    private static float[] CreateSineBuffer(int length)
    {
        var buffer = new float[length];
        for (int i = 0; i < length; i++)
            buffer[i] = MathF.Sin(i * 0.1f) * 0.5f;
        return buffer;
    }

    // GainProcessor

    [TestMethod(TestCategory.Unit, "GainProcessor SIMD path: 0 alloc", Group = TestGroups.Audio, Order = 1)]
    public static Task TestGainProcessorSimdZeroAllocAsync()
    {
        var processor = new GainProcessor();
        processor.SetVolumeGain(0.75f);

        float[] buffer = CreateSineBuffer(BufferSamples);

        // Прогрев: даём ramp завершиться до измерений
        for (int i = 0; i < 3000; i++)
            processor.Process(buffer.AsSpan());

        long allocated = MeasureAllocations(() => processor.Process(buffer.AsSpan()));

        Log.Info($"[HotPath] GainProcessor.Process (SIMD): {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "GainProcessor.Process (SIMD)");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor ramp path: 0 alloc", Group = TestGroups.Audio, Order = 2)]
    public static Task TestGainProcessorRampZeroAllocAsync()
    {
        var processor = new GainProcessor();
        float[] buffer = CreateSineBuffer(BufferSamples);
        bool toggle = false;

        long allocated = MeasureAllocations(() =>
        {
            // Переключаем gain каждую итерацию — держим ramp активным
            processor.SetVolumeGain(toggle ? 0.3f : 0.9f);
            toggle = !toggle;
            processor.Process(buffer.AsSpan());
        });

        Log.Info($"[HotPath] GainProcessor.Process (ramp): {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "GainProcessor.Process (ramp)");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor unity bypass: 0 alloc", Group = TestGroups.Audio, Order = 3)]
    public static Task TestGainProcessorUnityBypassZeroAllocAsync()
    {
        var processor = new GainProcessor();
        processor.SetVolumeGain(1.0f);

        float[] buffer = CreateSineBuffer(BufferSamples);

        // Прогрев: убеждаемся что ramp завершился и gain == 1.0
        for (int i = 0; i < 3000; i++)
            processor.Process(buffer.AsSpan());

        long allocated = MeasureAllocations(() => processor.Process(buffer.AsSpan()));

        Log.Info($"[HotPath] GainProcessor.Process (unity): {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "GainProcessor.Process (unity bypass)");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "SharedPlaybackState: Position interpolation & starvation clamp", Group = TestGroups.Audio, Order = 5)]
    public static async Task TestSharedPlaybackStateInterpolationAsync()
    {
        var sharedStateType = typeof(AudioPlayer).GetNestedType("SharedPlaybackState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("SharedPlaybackState type not found");

        var sharedState = Activator.CreateInstance(sharedStateType)!;
        var updateMethod = sharedStateType.GetMethod("Update")!;
        var getPositionMethod = sharedStateType.GetMethod("GetCurrentPosition")!;

        const int sampleRate = 48_000;
        const int channels = 2;
        const long durationMs = 180_000;
        const long baseSamples = sampleRate * channels * 10; // 10.0s
        const double basePositionSeconds = 10.0;

        var ring = new LockFreeRingBuffer<float>(BufferSamples * 4);
        float[] pcmChunk = CreateSineBuffer(BufferSamples); // 100ms данных в ring buffer

        // 1. Инвариант воспроизведения: пока в RingBuffer есть декодированные данные, интерполяция идет плавно
        ring.Write(pcmChunk.AsSpan());
        int activeBufferedSamples = ring.Count;

        updateMethod.Invoke(sharedState, [baseSamples, activeBufferedSamples, sampleRate, channels, true, durationMs]);
        await Task.Delay(25);

        var playingPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        double deltaMs = (playingPosition.TotalSeconds - basePositionSeconds) * 1000.0;

        if (deltaMs <= 0.0)
        {
            throw new InvalidOperationException(
                $"[Assertion Failed] Interpolation did not advance while playing. " +
                $"RingBuffer.Count={activeBufferedSamples}, delta={deltaMs:F2}ms.");
        }

        // 2. Инвариант Starvation Clamp: RingBuffer опустел (Count == 0) — дальнейшая экстраполяция заблокирована
        ring.Clear();
        int starvedBufferedSamples = ring.Count;
        long starvedBaseSamples = baseSamples + activeBufferedSamples;
        double starvedBaseSeconds = (double)starvedBaseSamples / (sampleRate * channels);

        updateMethod.Invoke(sharedState, [starvedBaseSamples, starvedBufferedSamples, sampleRate, channels, true, durationMs]);
        await Task.Delay(25);

        var starvedPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        double starvedDeltaMs = (starvedPosition.TotalSeconds - starvedBaseSeconds) * 1000.0;

        if (starvedDeltaMs > 0.001)
        {
            throw new InvalidOperationException(
                $"[Assertion Failed] Starvation clamp broken (slider ghosting)! " +
                $"Expected 0ms advance when RingBuffer.Count={starvedBufferedSamples}, got +{starvedDeltaMs:F2}ms.");
        }

        Log.Info($"[HotPath] SharedPlaybackState: active advance +{deltaMs:F1}ms, starvation clamp +{starvedDeltaMs:F2}ms ✓");
    }

    // LockFreeRingBuffer

    [TestMethod(TestCategory.Unit, "RingBuffer Write: 0 alloc", Group = TestGroups.Audio, Order = 10)]
    public static Task TestRingBufferWriteZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] data = CreateSineBuffer(BufferSamples);

        long allocated = MeasureAllocations(() =>
        {
            ring.Write(data.AsSpan());
            ring.Clear();
        });

        Log.Info($"[HotPath] RingBuffer.Write: {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "LockFreeRingBuffer.Write");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "RingBuffer Read: 0 alloc", Group = TestGroups.Audio, Order = 11)]
    public static Task TestRingBufferReadZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] src = CreateSineBuffer(BufferSamples);
        float[] dst = new float[BufferSamples];

        long allocated = MeasureAllocations(() =>
        {
            ring.Write(src.AsSpan());
            ring.Read(dst.AsSpan());
        });

        Log.Info($"[HotPath] RingBuffer.Read: {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "LockFreeRingBuffer.Read");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "RingBuffer properties: 0 alloc", Group = TestGroups.Audio, Order = 12)]
    public static Task TestRingBufferPropertiesZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 4);
        float[] data = new float[256];
        ring.Write(data.AsSpan());

        int sink = 0;
        long allocated = MeasureAllocations(() =>
        {
            sink += ring.Count;
            sink += ring.Available;
            sink += ring.ProducerCachedAvailable;
            _ = ring.IsEmpty;
        });

        Log.Info($"[HotPath] RingBuffer properties: {allocated} B / {MeasureIterations} calls (sink={sink})");
        AssertZeroAlloc(allocated, "LockFreeRingBuffer properties");

        return Task.CompletedTask;
    }

    // Шаг 1: Полная цепочка DSP AudioCallback (Limiter + Bypass Constant + Bypass Crossfade + 3Band)

    [TestMethod(TestCategory.Unit, "Full AudioCallback DSP chain: 0 alloc", Group = TestGroups.Audio, Order = 20)]
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

        // Прогрев всех трёх веток DSP (Limiter, Bypass Constant, Bypass Crossfade)
        for (int i = 0; i < 50; i++)
        {
            ring.Write(src.AsSpan());
            int read = ring.Read(dst.AsSpan());
            var samples = dst.AsSpan(0, read);

            // Ветка 1: Limiter active (normGain > 1.0f)
            analyzer.LockResolvedGain(1.25f);
            float normGain = analyzer.ProcessSamples();
            crossfader.SetTarget(normGain, SampleRate, Channels);
            limiter.Process(samples, ref crossfader);

            // Ветка 2: Bypass Constant (normGain <= 1.0f, crossfader inactive)
            SpanMathHelper.MultiplyByConstant(samples, 0.85f);

            // Ветка 3: Bypass Crossfade (normGain <= 1.0f, crossfader active)
            crossfader.Reset(0.5f);
            crossfader.SetTarget(0.85f, SampleRate, Channels);
            SpanMathHelper.MultiplyByCrossfade(samples, ref crossfader);

            analyzer3Band.ProcessBlock(samples, Channels);
            processor.Process(samples);
        }

        int branchSelector = 0;

        long allocated = MeasureAllocations(() =>
        {
            ring.Write(src.AsSpan());
            int read = ring.Read(dst.AsSpan());
            if (read > 0)
            {
                var samples = dst.AsSpan(0, read);
                int mode = branchSelector++ % 3;

                if (mode == 0)
                {
                    // Ветка с лимитером (normGain > 1.0f)
                    analyzer.LockResolvedGain(1.25f);
                }
                else if (mode == 1)
                {
                    // Ветка Bypass + Constant (normGain <= 1.0f, без рампы)
                    analyzer.LockResolvedGain(0.85f);
                    crossfader.Reset(0.85f);
                }
                else
                {
                    // Ветка Bypass + Crossfade (normGain <= 1.0f, активная рампа)
                    analyzer.LockResolvedGain(0.85f);
                    crossfader.Reset(0.5f);
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
        });

        Log.Info($"[HotPath] Full AudioCallback DSP chain (all 3 branches): {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "Full AudioCallback DSP chain");

        return Task.CompletedTask;
    }

    // Шаг 1b: KWeightingFilter.ProcessBlock (EBU R128 pre-scan hot path: 0 аллокаций)

    [TestMethod(TestCategory.Unit, "KWeightingFilter ProcessBlock: 0 alloc", Group = TestGroups.Audio, Order = 21)]
    public static Task TestKWeightingFilterZeroAllocAsync()
    {
        var filter = new KWeightingFilter(SampleRate, Channels);
        float[] input = CreateSineBuffer(960 * Channels);
        double[] output = new double[960 * Channels];

        long allocated = MeasureAllocations(() =>
        {
            filter.Reset();
            filter.ProcessBlock(input.AsSpan(), output.AsSpan());
        });

        Log.Info($"[HotPath] KWeightingFilter.ProcessBlock: {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "KWeightingFilter.ProcessBlock");

        return Task.CompletedTask;
    }

    // Шаг 2: SharedPlaybackState Update + GetCurrentPosition (0 аллокаций)

    [TestMethod(TestCategory.Unit, "SharedPlaybackState Update+Get: 0 alloc", Group = TestGroups.Audio, Order = 22)]
    public static Task TestSharedPlaybackStateZeroAllocAsync()
    {
        var sharedStateType = typeof(AudioPlayer).GetNestedType(
            "SharedPlaybackState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("SharedPlaybackState type not found");

        var instance = Activator.CreateInstance(sharedStateType)!;
        var updateMethod = sharedStateType.GetMethod("Update")!;
        var getPosMethod = sharedStateType.GetMethod("GetCurrentPosition")!;

        // Привязываем строго типизированные делегаты один раз, чтобы исключить рефлексию при замере
        var updateDel = (Action<long, int, int, int, bool, long>)Delegate.CreateDelegate(
            typeof(Action<long, int, int, int, bool, long>), instance, updateMethod);
        var getPosDel = (Func<TimeSpan>)Delegate.CreateDelegate(
            typeof(Func<TimeSpan>), instance, getPosMethod);

        long sampleCounter = 0;
        long ticksSink = 0;

        long allocated = MeasureAllocations(() =>
        {
            sampleCounter += 960;
            updateDel(sampleCounter, 9600, SampleRate, Channels, true, 180_000L);
            ticksSink += getPosDel().Ticks;
        });

        Log.Info($"[HotPath] SharedPlaybackState Update+Get: {allocated} B / {MeasureIterations} calls (sink={ticksSink})");
        AssertZeroAlloc(allocated, "SharedPlaybackState.Update + GetCurrentPosition");

        return Task.CompletedTask;
    }

    // Шаг 3: Парсинг Laced-блоков в WebMParser и WebMContainerParser (0 аллокаций)

    [TestMethod(TestCategory.Unit, "WebMParser & WebMContainerParser Xiph Lacing: 0 alloc", Group = TestGroups.Audio, Order = 23)]
    public static Task TestWebMParserLacingZeroAllocAsync()
    {
        ReadOnlySpan<byte> clusterHeader =
        [
            0x1F, 0x43, 0xB6, 0x75, // CLUSTER_ID (4B)
            0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // Unknown cluster size (8B)
            0xE7, 0x81, 0x00 // TIMECODE_ID (0xE7), size=1 (0x81), value=0 (0x00)
        ];

        ReadOnlySpan<byte> simpleBlock =
        [
            0xA3, 0x8E, // SIMPLE_BLOCK_ID (0xA3), size=14 bytes (0x8E)
            0x81,       // TrackNumber = 1 (VINT 1B)
            0x00, 0x00, // TimecodeOffset = 0 (2B)
            0x82,       // Flags: KeyFrame (0x80) | Xiph Lacing (0b01 << 1 = 0x02) -> 0x82
            0x01,       // Lace frame count minus 1 = 1 (всего 2 фрейма в блоке)
            0x04,       // Frame 0 size = 4 bytes (Frame 1 = оставшиеся 4 байта)
            0x11, 0x22, 0x33, 0x44, // Frame 0 payload (4B)
            0x55, 0x66, 0x77, 0x88  // Frame 1 payload (4B)
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

        int payloadSink = 0;
        long allocated = MeasureAllocations(() =>
        {
            var vt1 = containerParser.ReadNextFrameAsync();
            var f1 = vt1.IsCompletedSuccessfully ? vt1.Result : vt1.AsTask().GetAwaiter().GetResult();
            payloadSink += f1?.Data.Length ?? 0;

            var vt2 = containerParser.ReadNextFrameAsync();
            var f2 = vt2.IsCompletedSuccessfully ? vt2.Result : vt2.AsTask().GetAwaiter().GetResult();
            payloadSink += f2?.Data.Length ?? 0;
        });

        Log.Info($"[HotPath] WebMContainerParser Xiph Lacing: {allocated} B / {MeasureIterations} calls (sink={payloadSink})");
        AssertZeroAlloc(allocated, "WebMContainerParser.ReadNextFrameAsync (Xiph Lacing)");

        return Task.CompletedTask;
    }

    // Шаг 3b: Mp4ContainerParser.ReadNextFrameAsync и SIMD конвертация декодеров (0 аллокаций)

    [TestMethod(TestCategory.Unit, "Mp4ContainerParser ReadNextFrame & Decoder SIMD: 0 alloc", Group = TestGroups.Audio, Order = 24)]
    public static Task TestMp4ParserAndDecoderSimdZeroAllocAsync()
    {
        byte[] mdatPayload = new byte[4096];
        for (int i = 0; i < mdatPayload.Length; i++)
            mdatPayload[i] = (byte)(i & 0xFF);

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
        int sink = 0;

        long allocated = MeasureAllocations(() =>
        {
            var vt = mp4Parser.ReadNextFrameAsync();
            var frame = vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
            sink += frame?.Data.Length ?? 0;

            Core.Audio.Decoders.AacDecoder.ConvertBytesToFloat(pcm16Bytes, floatOut, 2048);
        });

        Log.Info($"[HotPath] Mp4ContainerParser + Decoder SIMD: {allocated} B / {MeasureIterations} calls (sink={sink})");
        AssertZeroAlloc(allocated, "Mp4ContainerParser.ReadNextFrameAsync + ConvertBytesToFloat");

        return Task.CompletedTask;
    }

    // Шаг 3c: OpusDecoder.Decode и PLC (Concentus hot path: 0 аллокаций)

    [TestMethod(TestCategory.Unit, "OpusDecoder Decode & PLC: 0 alloc", Group = TestGroups.Audio, Order = 25)]
    public static Task TestOpusDecoderZeroAllocAsync()
    {
        using var decoder = new Core.Audio.Decoders.OpusDecoder(SampleRate, Channels);

        // Валидный 20ms stereo CELT-only тихий пакет Opus (TOC = 0xF8: FB, 20ms, 1 frame, stereo = 0xFC)
        byte[] validOpusFrame = [0xFC, 0xFF, 0xFE];
        float[] pcmOut = new float[decoder.MaxFrameSize * Channels];
        int samplesSink = 0;

        long allocated = MeasureAllocations(() =>
        {
            samplesSink += decoder.Decode(validOpusFrame.AsSpan(), pcmOut.AsSpan());
            samplesSink += decoder.Decode(ReadOnlySpan<byte>.Empty, pcmOut.AsSpan());
        });

        Log.Info($"[HotPath] OpusDecoder.Decode + PLC: {allocated} B / {MeasureIterations} calls (sink={samplesSink})");
        AssertZeroAlloc(allocated, "OpusDecoder.Decode + PLC");

        // Проверяем, что PLC сохраняет точную длительность предыдущего пакета на других частотах (24 kHz -> 480 сэмплов для 20 мс)
        using var decoder24k = new Core.Audio.Decoders.OpusDecoder(24000, Channels);
        int dec24 = decoder24k.Decode(validOpusFrame.AsSpan(), pcmOut.AsSpan());
        int plc24 = decoder24k.Decode([], pcmOut.AsSpan());
        if (dec24 != 480 || plc24 != 480)
        {
            throw new InvalidOperationException(
                $"[Assertion Failed] OpusDecoder 24kHz PLC frame size mismatch: expected 480, got decode={dec24}, plc={plc24}");
        }

        return Task.CompletedTask;
    }

    // Шаг 4: AsyncCachingReadStream.ReadAsync и обёртки IAudioSource.ReadFrameAsync (0 аллокаций)

    [TestMethod(TestCategory.Unit, "AsyncCachingReadStream & IAudioSource.ReadFrameAsync: 0 alloc", Group = TestGroups.Audio, Order = 26)]
    public static Task TestAsyncCachingReadStreamRamHitZeroAllocAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_TestCache_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // Готовим поток с WebM Cluster + SimpleBlock для сквозного чтения через IAudioSource.ReadFrameAsync
            ReadOnlySpan<byte> clusterHeader =
            [
                0x1F, 0x43, 0xB6, 0x75,
                0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                0xE7, 0x81, 0x00
            ];

            ReadOnlySpan<byte> simpleBlock =
            [
                0xA3, 0x88, // SIMPLE_BLOCK_ID, size=8
                0x81,       // Track 1
                0x00, 0x00, // Timecode 0
                0x80,       // KeyFrame, no lacing
                0xFC, 0xFF, 0xFE, 0x00 // 4B payload
            ];

            int totalFrames = (WarmupIterations + MeasureIterations) * 2 + 16;
            byte[] streamData = new byte[clusterHeader.Length + totalFrames * simpleBlock.Length];
            clusterHeader.CopyTo(streamData);
            int offset = clusterHeader.Length;
            for (int i = 0; i < totalFrames; i++)
            {
                simpleBlock.CopyTo(streamData.AsSpan(offset));
                offset += simpleBlock.Length;
            }

            string localFilePath = Path.Combine(tempDir, "local_frames.webm");
            File.WriteAllBytes(localFilePath, streamData);

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
                config: StreamingProfiles.Medium);

            // Помещаем данные в SlidingRamCache для 100% RAM hit
            var ramCacheField = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_ramCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var ramCache = ramCacheField.GetValue(source)!;

            using var owner = System.Buffers.MemoryPool<byte>.Shared.Rent(streamData.Length);
            streamData.CopyTo(owner.Memory.Span);

            var blockType = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetNestedType("RamRangeBlock", System.Reflection.BindingFlags.NonPublic)!;
            var block = (IDisposable)Activator.CreateInstance(blockType, [0L, owner, streamData.Length])!;

            var tryAddMethod = ramCache.GetType().GetMethod("TryAdd")!;
            tryAddMethod.Invoke(ramCache, [block]);

            var streamType = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetNestedType("AsyncCachingReadStream", System.Reflection.BindingFlags.NonPublic)!;
            using var readStream = (Stream)Activator.CreateInstance(streamType, [source])!;
            using var cachingParser = new Core.Audio.Parsers.WebMContainerParser(readStream);

            // Привязываем _readStream, _parser и _initialized к CachingStreamSource
            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_readStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, readStream);
            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_parser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, cachingParser);
            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, true);

            // Настраиваем LocalFileSource для сквозного теста ReadFrameAsync
            using var localSource = new Core.Audio.Sources.LocalFileSource(localFilePath);
            using var localFs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var localParser = new Core.Audio.Parsers.WebMContainerParser(localFs);

            typeof(Core.Audio.Sources.LocalFileSource)
                .GetField("_fileStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(localSource, localFs);
            typeof(Core.Audio.Sources.LocalFileSource)
                .GetField("_parser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(localSource, localParser);
            typeof(Core.Audio.Sources.LocalFileSource)
                .GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(localSource, true);

            using var cts = new CancellationTokenSource();
            var ct = cts.Token;
            int sink = 0;

            long allocated = MeasureAllocations(() =>
            {
                var vtCaching = source.ReadFrameAsync(ct);
                var frame1 = vtCaching.IsCompletedSuccessfully ? vtCaching.Result : vtCaching.AsTask().GetAwaiter().GetResult();
                sink += frame1?.Data.Length ?? 0;

                var vtLocal = localSource.ReadFrameAsync(ct);
                var frame2 = vtLocal.IsCompletedSuccessfully ? vtLocal.Result : vtLocal.AsTask().GetAwaiter().GetResult();
                sink += frame2?.Data.Length ?? 0;
            });

            Log.Info($"[HotPath] IAudioSource.ReadFrameAsync (Caching + Local): {allocated} B / {MeasureIterations} calls (sink={sink})");
            AssertZeroAlloc(allocated, "CachingStreamSource.ReadFrameAsync + LocalFileSource.ReadFrameAsync");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    // Шаг 5: GetBufferedRanges (complete + partial cached) и проверка флага _dirty в AudioCacheManager

    [TestMethod(TestCategory.Unit, "GetBufferedRanges (complete & partial) & Cache dirty-check: 0 alloc", Group = TestGroups.Audio, Order = 27)]
    public static async Task TestGetBufferedRangesAndCacheDirtyZeroAllocAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_TestRanges_" + Guid.NewGuid().ToString("N"));
        string dummyFile = Path.Combine(tempDir, "dummy.webm");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(dummyFile, [0x1A, 0x45, 0xDF, 0xA3]);

            using var localSource = new Core.Audio.Sources.LocalFileSource(dummyFile);
            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);

            // 1. Полностью скачанная запись
            var completeEntry = cacheManager.CreateOrUpdate(
                "test_complete_key",
                "yt_test_complete",
                "https://example.com/audio.webm",
                totalSize: 4,
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128);

            await cacheManager.WriteRangeAsync("test_complete_key", 0, new byte[] { 1, 2, 3, 4 });

            using var completeCachingSource = new Core.Audio.Sources.CachingStreamSource(
                cacheKey: "test_complete_key",
                trackId: "yt_test_complete",
                url: "https://example.com/audio.webm",
                contentLength: 4,
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128,
                cacheManager: cacheManager,
                config: StreamingProfiles.Medium);

            var cacheEntryField = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_cacheEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            cacheEntryField.SetValue(completeCachingSource, completeEntry);

            // 2. Частично скачанная запись (50% на диске)
            var partialEntry = cacheManager.CreateOrUpdate(
                "test_partial_key",
                "yt_test_partial",
                "https://example.com/partial.webm",
                totalSize: 8,
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128);

            await cacheManager.WriteRangeAsync("test_partial_key", 0, new byte[] { 1, 2, 3, 4 });

            using var partialCachingSource = new Core.Audio.Sources.CachingStreamSource(
                cacheKey: "test_partial_key",
                trackId: "yt_test_partial",
                url: "https://example.com/partial.webm",
                contentLength: 8,
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128,
                cacheManager: cacheManager,
                config: StreamingProfiles.Medium);

            cacheEntryField.SetValue(partialCachingSource, partialEntry);

            int rangeCountSink = 0;
            long allocated = MeasureAllocations(() =>
            {
                rangeCountSink += localSource.GetBufferedRanges().Count;
                rangeCountSink += completeCachingSource.GetBufferedRanges().Count;
                rangeCountSink += partialCachingSource.GetBufferedRanges().Count;
            });

            Log.Info($"[HotPath] GetBufferedRanges (local + complete + partial): {allocated} B / {MeasureIterations} calls (sink={rangeCountSink})");
            AssertZeroAlloc(allocated, "GetBufferedRanges (LocalFileSource + CachingStreamSource complete & partial)");

            // Проверяем, что при добавлении нового диапазона кэш инвалидируется и обновляет список
            await cacheManager.WriteRangeAsync("test_partial_key", 4, new byte[] { 5, 6 });
            var updatedRanges = partialCachingSource.GetBufferedRanges();
            if (updatedRanges.Count != 1 || Math.Abs(updatedRanges[0].End - 0.75) > 0.001)
            {
                throw new InvalidOperationException(
                    $"[Assertion Failed] Partial GetBufferedRanges did not invalidate after WriteRangeAsync! End={updatedRanges[0].End}");
            }

            // Проверяем, что AudioCacheManager не перезаписывает индекс на диск без изменений (_dirty == 0)
            var saveMethod = typeof(Core.Audio.Cache.AudioCacheManager)
                .GetMethod("SaveIndexSync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            saveMethod.Invoke(cacheManager, null);

            string binPath = Path.Combine(tempDir, "cache_index.bin");
            var firstWriteTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(binPath, firstWriteTime);

            saveMethod.Invoke(cacheManager, null);
            var secondWriteTime = File.GetLastWriteTimeUtc(binPath);

            if (secondWriteTime != firstWriteTime)
            {
                throw new InvalidOperationException(
                    "[Assertion Failed] AudioCacheManager.SaveIndexSync overwrote cache_index.bin when no entries were modified!");
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Шаг 6: Сквозной интеграционный Zero-Alloc тест всего конвейера (Source -> Parser(Laced) -> Decoder -> RingBuffer -> DSP -> Gain -> PlayerEvents -> Seek)

    [TestMethod(TestCategory.Unit, "End-to-End Pipeline Step (Source→Decoder→Ring→DSP→Events→Seek): 0 alloc", Group = TestGroups.Audio, Order = 28)]
    public static Task TestEndToEndPipelineStepZeroAllocAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_TestE2E_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // Валидный WebM поток с CuePoint (для SeekAsync(0)) и Xiph-laced блоками по 2 Opus-фрейма (20ms stereo CELT silence: 0xFC, 0xFF, 0xFE)
            ReadOnlySpan<byte> ebmlAndCuesAndClusterHeader =
            [
                // Cues element (ID = 0x1C53BB6B, size = 15 bytes -> 0x8F)
                0x1C, 0x53, 0xBB, 0x6B, 0x8F,
                // CuePoint (ID = 0xBB, size = 13 bytes -> 0x8D)
                0xBB, 0x8D,
                // CueTime (ID = 0xB3, size = 1 -> 0x81, value = 0)
                0xB3, 0x81, 0x00,
                // CueTrackPositions (ID = 0xB7, size = 8 -> 0x88)
                0xB7, 0x88,
                // CueClusterPosition (ID = 0xF1, size = 1 -> 0x81, offset = 20 -> 0x14, прямо на начало CLUSTER_ID)
                0xF1, 0x81, 0x14,
                // Padding внутри CueTrackPositions (Void ID = 0xEC, size = 3 -> 0x83, 3 zero bytes)
                0xEC, 0x83, 0x00, 0x00, 0x00,

                // Cluster (начинается со смещения 20 = 0x14)
                0x1F, 0x43, 0xB6, 0x75, // CLUSTER_ID
                0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // Unknown cluster size
                0xE7, 0x81, 0x00 // TIMECODE_ID = 0
            ];

            ReadOnlySpan<byte> lacedSimpleBlock =
            [
                0xA3, 0x8C, // SIMPLE_BLOCK_ID (0xA3), size = 12 bytes (0x8C)
                0x81,       // TrackNumber = 1
                0x00, 0x00, // TimecodeOffset = 0
                0x82,       // Flags: KeyFrame (0x80) | Xiph Lacing (0x02)
                0x01,       // Lace frame count minus 1 = 1 (2 фрейма в блоке)
                0x03,       // Frame 0 size = 3 bytes (Frame 1 = оставшиеся 3 байта)
                0xFC, 0xFF, 0xFE, // Frame 0: Valid 3-byte Opus 20ms stereo frame
                0xFC, 0xFF, 0xFE  // Frame 1: Valid 3-byte Opus 20ms stereo frame
            ];

            // Создаём 256 laced-блоков (512 фреймов), чтобы за 2000 итераций гарантированно сработал периодический SeekAsync(0)
            const int blocksInStream = 256;
            byte[] streamData = new byte[ebmlAndCuesAndClusterHeader.Length + blocksInStream * lacedSimpleBlock.Length];
            ebmlAndCuesAndClusterHeader.CopyTo(streamData);

            int offset = ebmlAndCuesAndClusterHeader.Length;
            for (int i = 0; i < blocksInStream; i++)
            {
                lacedSimpleBlock.CopyTo(streamData.AsSpan(offset));
                offset += lacedSimpleBlock.Length;
            }

            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);
            var entry = cacheManager.CreateOrUpdate(
                "e2e_key",
                "yt_e2e",
                "https://example.com/e2e.webm",
                totalSize: streamData.Length * 2, // Partial cache state
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128);

            using var source = new Core.Audio.Sources.CachingStreamSource(
                cacheKey: "e2e_key",
                trackId: "yt_e2e",
                url: "https://example.com/e2e.webm",
                contentLength: streamData.Length * 2,
                format: AudioFormat.WebM,
                codec: Core.Audio.Interfaces.AudioCodec.Opus,
                bitrate: 128,
                cacheManager: cacheManager,
                config: StreamingProfiles.Medium);

            // Загружаем весь тестовый WebM поток в RAM-кэш источника
            var ramCacheField = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_ramCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var ramCache = ramCacheField.GetValue(source)!;

            using var owner = System.Buffers.MemoryPool<byte>.Shared.Rent(streamData.Length);
            streamData.CopyTo(owner.Memory.Span);

            var blockType = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetNestedType("RamRangeBlock", System.Reflection.BindingFlags.NonPublic)!;
            var block = (IDisposable)Activator.CreateInstance(blockType, [0L, owner, streamData.Length])!;
            ramCache.GetType().GetMethod("TryAdd")!.Invoke(ramCache, [block]);

            var streamType = typeof(Core.Audio.Sources.CachingStreamSource)
                .GetNestedType("AsyncCachingReadStream", System.Reflection.BindingFlags.NonPublic)!;
            using var readStream = (Stream)Activator.CreateInstance(streamType, [source])!;
            using var parser = new Core.Audio.Parsers.WebMContainerParser(readStream);

            // Парсим CuePoints для работы FindSeekPosition(0)
            var parsed = parser.ParseHeadersAsync().GetAwaiter().GetResult();
            if (!parsed)
                throw new InvalidOperationException("Failed to parse synthetic WebM headers with Cues");

            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_cacheEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, entry);
            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_readStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, readStream);
            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_parser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, parser);
            typeof(Core.Audio.Sources.CachingStreamSource)
                .GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(source, true);

            using var decoder = new Core.Audio.Decoders.OpusDecoder(SampleRate, Channels);
            var pcmRing = new LockFreeRingBuffer<float>(BufferSamples * 4);
            var analyzer = new Core.Audio.Normalization.EbuR128Analyzer();
            analyzer.Configure(new Core.Audio.Normalization.NormalizationConfig(
                Enabled: true,
                TargetLufs: -14f,
                MaxGain: 3.0f,
                Mode: Core.Audio.Normalization.NormalizationMode.Bidirectional));
            analyzer.LockResolvedGain(1.15f);

            var limiter = new Core.Audio.Normalization.TruePeakLimiter(SampleRate);
            var analyzer3Band = new Audio3BandAnalyzer(SampleRate, Channels);
            var crossfader = new Core.Audio.Normalization.GainCrossfader(1.15f);
            var gainProcessor = new GainProcessor();
            gainProcessor.SetVolumeGain(0.85f);

            using var player = new AudioPlayer(new AudioPlayerOptions { UseNullBackend = true });
            long eventsSink = 0;
            player.Events.PositionChanged += pos => eventsSink += pos.Ticks;
            player.Events.BufferStateChanged += state => eventsSink += state.Ranges.Count;

            var raisePosMethod = typeof(AudioPlayerEvents)
                .GetMethod("RaisePositionChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var raiseBufMethod = typeof(AudioPlayerEvents)
                .GetMethod("RaiseBufferState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var raisePosDel = (Action<TimeSpan>)Delegate.CreateDelegate(typeof(Action<TimeSpan>), player.Events, raisePosMethod);
            var raiseBufDel = (Action<BufferState>)Delegate.CreateDelegate(typeof(Action<BufferState>), player.Events, raiseBufMethod);

            float[] decodeBuffer = new float[decoder.MaxFrameSize * Channels];
            float[] backendBuffer = new float[960 * Channels];

            using var cts = new CancellationTokenSource();
            var ct = cts.Token;

            // Прогрев GainProcessor ramp до стационарного состояния
            for (int i = 0; i < 3000; i++)
                gainProcessor.Process(backendBuffer.AsSpan());

            int totalRenderedSamples = 0;
            int framesRead = 0;

            long allocated = MeasureAllocations(() =>
            {
                // Периодический сброс позиции на начало кластера при чтении 400 фреймов (проверка отсутствия утечек PooledFrameOwner при Reset)
                if (++framesRead >= 400)
                {
                    framesRead = 0;
                    readStream.Position = 20; // Смещение начала CLUSTER_ID
                    parser.Reset();
                }

                // 1. Producer step (DecoderLoopAsync): Source.ReadFrameAsync (Xiph Laced) -> OpusDecoder.Decode -> RingBuffer.Write
                var vt = source.ReadFrameAsync(ct);
                var frame = vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();

                if (frame != null)
                {
                    int samplesPerChannel = decoder.Decode(frame.Value.Data.Span, decodeBuffer.AsSpan());
                    int writtenSamples = samplesPerChannel * Channels;
                    pcmRing.Write(decodeBuffer.AsSpan(0, writtenSamples));
                }

                // 2. Consumer step (AudioCallback): RingBuffer.Read -> EbuR128 -> Limiter -> 3Band -> GainProcessor
                int read = pcmRing.Read(backendBuffer.AsSpan());
                if (read > 0)
                {
                    var samples = backendBuffer.AsSpan(0, read);
                    float normGain = analyzer.ProcessSamples();
                    crossfader.SetTarget(normGain, SampleRate, Channels);
                    limiter.Process(samples, ref crossfader);
                    analyzer3Band.ProcessBlock(samples, Channels);
                    gainProcessor.Process(samples);
                    totalRenderedSamples += read;
                }

                // 3. UI Timer & Events step: Position + RaisePositionChanged + RaiseBufferState(GetBufferedRanges)
                var pos = player.Position;
                raisePosDel(pos);
                var ranges = source.GetBufferedRanges();
                raiseBufDel(new BufferState(source.BufferProgress, source.IsFullyBuffered, ranges, 0, 0));
                totalRenderedSamples += ranges.Count;
            });

            Log.Info($"[HotPath] End-to-End Pipeline Step (Laced+Reset+Events): {allocated} B / {MeasureIterations} calls (rendered={totalRenderedSamples}, events={eventsSink})");
            AssertZeroAlloc(allocated, "End-to-End Pipeline Step (Source→LacedParser→OpusDecoder→RingBuffer→DSP→Gain→Events)");

            return Task.CompletedTask;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Шаг 7: Проверка адаптивных задержек DecoderLoop и подавления UI-таймеров в Suspend

    [TestMethod(TestCategory.Unit, "Adaptive Decoder Delay & UI Timers Suspend: 0 alloc", Group = TestGroups.Audio, Order = 29)]
    public static Task TestAdaptiveDecoderDelayAndTimerSuspendAsync()
    {
        // 1. Проверяем шкалу задержек декодера при разном заполнении 2-секундного буфера (48kHz stereo = 96000 сэмплов/сек)
        int delayEmpty = AudioPipeline.ComputeBufferFullDelayMs(0, SampleRate, Channels);
        int delayLow = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 200 / 1000, SampleRate, Channels); // 200ms
        int delayMid = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 500 / 1000, SampleRate, Channels); // 500ms
        int delayHigh = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 1500 / 1000, SampleRate, Channels); // 1500ms

        if (delayEmpty != 5 || delayLow != 5 || delayMid != 25 || delayHigh != 180)
        {
            throw new InvalidOperationException(
                $"[Assertion Failed] Unexpected adaptive decoder delays: empty={delayEmpty}ms, low={delayLow}ms, mid={delayMid}ms, high={delayHigh}ms");
        }

        // 2. Проверяем пакетный гистерезис декодера (Batch Low Watermark):
        // за 1 секунду аудио (48kHz stereo, 10 тиков по 100мс от WinAudioBackend) декодер должен просыпаться <= 3 раз, а не 50-200 раз!
        var ring = new LockFreeRingBuffer<float>(SampleRate * Channels * 2); // 2 сек буфер (степень двойки = 262144)
        int frameSize = 960 * Channels; // 20ms Opus frame = 1920 float
        int batchRefillSpace = Math.Max(frameSize * 4, ring.Capacity / 4);
        float[] frameBuf = new float[frameSize];
        float[] backendChunk = new float[SampleRate * Channels / 10]; // 100ms WinMM chunk = 9600 float

        // Заполняем буфер полностью, как на старте трека
        while (ring.Available >= frameSize)
            ring.Write(frameBuf.AsSpan());

        int decoderWakeups = 0;
        for (int tick = 0; tick < 10; tick++) // Симулируем 1 секунду воспроизведения (10 тиков WinMM по 100 мс)
        {
            ring.Read(backendChunk.AsSpan());

            // Декодер просыпается ТОЛЬКО когда освободилось >= batchRefillSpace (Low Watermark)
            if (ring.Available >= batchRefillSpace)
            {
                decoderWakeups++;
                while (ring.Available >= frameSize)
                    ring.Write(frameBuf.AsSpan());
            }
        }

        if (decoderWakeups > 3)
        {
            throw new InvalidOperationException(
                $"[Assertion Failed] Excessive decoder wakeups per second: expected <= 3 with batch hysteresis, got {decoderWakeups}");
        }

        // 3. Проверяем подавление UI-таймеров в AudioPlayer при переводе в фоновый режим и 0 аллокаций
        using var player = new AudioPlayer(new AudioPlayerOptions { UseNullBackend = true });
        int sink = 0;

        long allocated = MeasureAllocations(() =>
        {
            sink += AudioPipeline.ComputeBufferFullDelayMs(144_000, SampleRate, Channels);
            player.SetUiTimersSuspended(true);
            player.SetUiTimersSuspended(false);
        });

        Log.Info($"[HotPath] Adaptive Decoder Delay & Timer Suspend: {allocated} B / {MeasureIterations} calls, wakeups={decoderWakeups}/sec (sink={sink})");
        AssertZeroAlloc(allocated, "ComputeBufferFullDelayMs + SetUiTimersSuspended");

        return Task.CompletedTask;
    }

    // Шаг 8: Ядерная диагностика Context Switches по каждому потоку процесса через NtQuerySystemInformation

    [TestMethod(TestCategory.Unit, "Per-Thread Context Switches Diagnostic (Windows NT Kernel)", Group = TestGroups.Audio, Order = 30)]
    public static async Task TestThreadContextSwitchesDiagnosticAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var snapBefore = CaptureProcessThreadsSnapshot();
        await Task.Delay(2000).ConfigureAwait(false);
        var snapAfter = CaptureProcessThreadsSnapshot();

        var deltas = new List<(int ThreadId, uint CsDelta, double CpuMs, int Priority, int WaitReason, ulong StartAddr)>();
        uint totalCs = 0;

        foreach (var (tid, after) in snapAfter)
        {
            if (snapBefore.TryGetValue(tid, out var before))
            {
                uint delta = after.ContextSwitches - before.ContextSwitches;
                if (delta > 0)
                {
                    totalCs += delta;
                    double cpuDeltaMs = (after.KernelTime + after.UserTime - (before.KernelTime + before.UserTime)) / 10_000.0;
                    deltas.Add((tid, delta, cpuDeltaMs, after.Priority, after.WaitReason, after.StartAddress));
                }
            }
        }

        deltas.Sort(static (a, b) => b.CsDelta.CompareTo(a.CsDelta));

        Log.Info($"[CS-Diagnostic] ═══ Per-Thread Context Switches over 2.0s (Total: {totalCs} CS = {totalCs / 2.0:F1} CS/sec) ═══");
        foreach (var item in deltas.Take(15))
        {
            string moduleName = ResolveAddressModuleName(item.StartAddr);
            Log.Info($"[CS-Diagnostic]   TID={item.ThreadId,6} │ {item.CsDelta / 2.0,7:F1} CS/s ({item.CsDelta,5} total) │ CPU={item.CpuMs,6:F1}ms │ Prio={item.Priority,2} │ Wait={item.WaitReason,2} │ Start={moduleName}");
        }
        Log.Info("[CS-Diagnostic] ═══════════════════════════════════════════════════════════════════════════════════");
    }

    private readonly record struct ThreadKernelInfo(
        uint ContextSwitches,
        long KernelTime,
        long UserTime,
        int Priority,
        int WaitReason,
        ulong StartAddress);

    private static unsafe Dictionary<int, ThreadKernelInfo> CaptureProcessThreadsSnapshot()
    {
        var result = new Dictionary<int, ThreadKernelInfo>(64);
        int currentPid = Environment.ProcessId;

        int bufferSize = 512 * 1024;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            fixed (byte* pBuffer = buffer)
            {
                while (true)
                {
                    int status = NtQuerySystemInformation(5, (nint)pBuffer, buffer.Length, out int needed);
                    if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                        bufferSize = Math.Max(needed + 64 * 1024, bufferSize * 2);
                        buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
                        return CaptureProcessThreadsSnapshotWithSize(bufferSize);
                    }
                    if (status != 0) return result;
                    break;
                }
            }

            ParseSystemProcessInformation(buffer, currentPid, result);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private static unsafe Dictionary<int, ThreadKernelInfo> CaptureProcessThreadsSnapshotWithSize(int bufferSize)
    {
        var result = new Dictionary<int, ThreadKernelInfo>(64);
        int currentPid = Environment.ProcessId;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            fixed (byte* pBuffer = buffer)
            {
                int status = NtQuerySystemInformation(5, (nint)pBuffer, buffer.Length, out _);
                if (status == 0)
                    ParseSystemProcessInformation(buffer, currentPid, result);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return result;
    }

    private static void ParseSystemProcessInformation(byte[] buffer, int currentPid, Dictionary<int, ThreadKernelInfo> result)
    {
        ReadOnlySpan<byte> span = buffer;
        int offset = 0;

        // На Windows x64 sizeof(SYSTEM_PROCESS_INFORMATION) == 256 (0x100), включая IO_COUNTERS (с 0xD0 по 0x100).
        // Смещения внутри SYSTEM_THREAD_INFORMATION (80 байт):
        // +0x00: KernelTime (8B), +0x08: UserTime (8B), +0x10: CreateTime (8B), +0x18: WaitTime (4B + 4B pad)
        // +0x20 (32): StartAddress (8B), +0x28 (40): UniqueProcess (8B), +0x30 (48): UniqueThread / TID (8B)
        // +0x38 (56): Priority (4B), +0x3C (60): BasePriority (4B), +0x40 (64): ContextSwitches (4B), +0x48 (72): WaitReason (4B)
        const int SystemProcessInfoSizeX64 = 256;
        const int SystemThreadInfoSizeX64 = 80;

        while (offset + SystemProcessInfoSizeX64 <= span.Length)
        {
            uint nextEntryOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            uint numberOfThreads = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
            nint uniqueProcessId = (nint)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 80, 8));

            if (uniqueProcessId == currentPid)
            {
                int threadOffset = offset + SystemProcessInfoSizeX64;
                for (uint i = 0; i < numberOfThreads && threadOffset + SystemThreadInfoSizeX64 <= span.Length; i++, threadOffset += SystemThreadInfoSizeX64)
                {
                    long kernelTime = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset, 8));
                    long userTime = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 8, 8));
                    ulong startAddr = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(threadOffset + 32, 8));
                    int tid = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 48, 8));
                    int priority = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(threadOffset + 56, 4));
                    uint contextSwitches = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(threadOffset + 64, 4));
                    int waitReason = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(threadOffset + 72, 4));

                    result[tid] = new ThreadKernelInfo(contextSwitches, kernelTime, userTime, priority, waitReason, startAddr);
                }
                break;
            }

            if (nextEntryOffset == 0) break;
            offset += (int)nextEntryOffset;
        }
    }

    private static string ResolveAddressModuleName(ulong address)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule mod in proc.Modules)
            {
                ulong baseAddr = (ulong)mod.BaseAddress;
                ulong endAddr = baseAddr + (ulong)mod.ModuleMemorySize;
                if (address >= baseAddr && address < endAddr)
                    return $"{mod.ModuleName}+0x{address - baseAddr:X}";
            }
        }
        catch { }
        return $"0x{address:X}";
    }

    [System.Runtime.InteropServices.LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(
        int systemInformationClass,
        nint systemInformation,
        int systemInformationLength,
        out int returnLength);
}