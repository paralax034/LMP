using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Детерминированные модульные тесты алгоритмов аудио-тракта и DSP:
/// проверяют математические инварианты (ITU-R BS.1770-4, побитовую линейность,
/// фазовую когерентность каналов, частотную избирательность, асимптотический потолок),
/// конечный автомат состояний и отсутствие аллокаций (0 B) на горячем пути.
/// </summary>
public static class AudioLogicTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 мс стерео

    [TestMethod(TestCategory.Unit, "Position Interpolation & Starvation Clamp", Group = TestGroups.Audio, Order = 1)]
    public static async Task TestSharedPlaybackStateInterpolationAsync()
    {
        var sharedStateType = typeof(AudioPlayer).GetNestedType(
            "SharedPlaybackState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("SharedPlaybackState type not found");

        var sharedState = Activator.CreateInstance(sharedStateType)!;
        var updateMethod = sharedStateType.GetMethod("Update")!;
        var getPositionMethod = sharedStateType.GetMethod("GetCurrentPosition")!;

        const long durationMs = 180_000;
        const long baseSamples = SampleRate * Channels * 10;
        const double basePositionSeconds = 10.0;

        using var ring = new LockFreeRingBuffer<float>(BufferSamples * 4);
        float[] pcmChunk = new float[BufferSamples];

        // 1. Монотонный ход интерполяции при наличии данных в кольцевом буфере
        ring.Write(pcmChunk.AsSpan());
        int activeBufferedSamples = ring.Count;

        updateMethod.Invoke(sharedState, [baseSamples, activeBufferedSamples, SampleRate, Channels, true, durationMs]);
        await Task.Delay(25).ConfigureAwait(false);

        var playingPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        double deltaMs = (playingPosition.TotalSeconds - basePositionSeconds) * 1000.0;
        TestAssert.True(deltaMs > 0.0, "Position must advance while playing with buffered samples");

        // 2. Starvation Clamp: при пустом кольцевом буфере интерполяция обязана мгновенно остановиться
        ring.Clear();
        long starvedBaseSamples = baseSamples + activeBufferedSamples;
        double starvedBaseSeconds = (double)starvedBaseSamples / (SampleRate * Channels);

        updateMethod.Invoke(sharedState, [starvedBaseSamples, ring.Count, SampleRate, Channels, true, durationMs]);
        await Task.Delay(25).ConfigureAwait(false);

        var starvedPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        double starvedDeltaMs = Math.Abs((starvedPosition.TotalSeconds - starvedBaseSeconds) * 1000.0);
        TestAssert.True(starvedDeltaMs <= 0.001, $"Starvation clamp failed: drift={starvedDeltaMs:F4}ms");

        // 3. Upper Bound Clamp: позиция никогда не должна превышать длительность трека
        long nearEndSamples = (long)(durationMs / 1000.0 * SampleRate * Channels);
        updateMethod.Invoke(sharedState, [nearEndSamples, activeBufferedSamples, SampleRate, Channels, true, durationMs]);
        await Task.Delay(20).ConfigureAwait(false);

        var clampedPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        TestAssert.True(clampedPosition.TotalMilliseconds <= durationMs, "Position exceeded track duration");
    }

    [TestMethod(TestCategory.Unit, "Adaptive Decoder Delay & State Machine Contracts", Group = TestGroups.Audio, Order = 2)]
    public static Task TestAdaptiveDecoderDelayAndBatchHysteresisAsync()
    {
        // 1. Монотонная шкала адаптивных задержек декодера в зависимости от запаса PCM в буфере
        int delayEmpty = AudioPipeline.ComputeBufferFullDelayMs(0, SampleRate, Channels);
        int delayLow = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 200 / 1000, SampleRate, Channels);
        int delayMid = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 500 / 1000, SampleRate, Channels);
        int delayHigh = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 1100 / 1000, SampleRate, Channels);
        int delayMax = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 1500 / 1000, SampleRate, Channels);

        TestAssert.Equal(5, delayEmpty);
        TestAssert.Equal(5, delayLow);
        TestAssert.Equal(25, delayMid);
        TestAssert.Equal(100, delayHigh);
        TestAssert.Equal(180, delayMax);

        // 2. Проверка контрактов конечного автомата PlayerStateTransitions
        TestAssert.True(
            PlayerStateTransitions.CanAcceptCommand(PlayerState.Playing, new StarvationCommand(1, null!)),
            "StarvationCommand must be accepted in Playing state");
        TestAssert.True(
            PlayerStateTransitions.CanAcceptCommand(PlayerState.Buffering, new StarvationCommand(1, null!)),
            "StarvationCommand must be accepted in Buffering state");
        TestAssert.False(
            PlayerStateTransitions.CanAcceptCommand(PlayerState.Disposed, new PlayCommand(default, 1)),
            "Disposed state must reject all commands except DisposeCommand");
        TestAssert.True(
            PlayerStateTransitions.CanAcceptCommand(PlayerState.Disposed, new DisposeCommand(1)),
            "Disposed state must accept DisposeCommand");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "WASAPI Zero-Copy Native Span DSP & Fade Envelope (0 Alloc)", Group = TestGroups.Audio, Order = 3)]
    [SupportedOSPlatform("windows")]
    public static unsafe Task TestWasapiZeroCopyNativeSpanDspAsync()
    {
        if (!OperatingSystem.IsWindows())
            TestAssert.Skip("Windows-only WASAPI test");

        const int frames = 960; // Полная длина микро-фейда (20 мс @ 48 кГц)
        const int sampleCount = frames * Channels;

        float* nativeBuffer = (float*)NativeMemory.AllocZeroed((nuint)(sampleCount * sizeof(float)));
        try
        {
            using var ring = new LockFreeRingBuffer<float>(sampleCount * 4);
            var gainProcessor = new GainProcessor();
            gainProcessor.SetVolumeGain(0.8f);

            float[] sourceSamples = new float[sampleCount];
            Array.Fill(sourceSamples, 0.5f);

            // 1. Математическая проверка Fade-In от 0 до 1 (с учётом IEEE 754 округления шага 1.0f / 960)
            Span<float> fadeSpan = new(nativeBuffer, sampleCount);
            fadeSpan.Fill(1.0f);
            float fadeGain = 0f;

            _ = WinAudioBackend.ApplyFadeEnvelope(
                fadeSpan,
                frames,
                Channels,
                WinAudioBackend.FadeState.FadingIn,
                ref fadeGain);

            TestAssert.True(MathF.Abs(fadeGain - 1.0f) < 1e-5f, $"Fade-in after 960 frames must reach ~1.0f, got {fadeGain:R}");
            TestAssert.True(fadeSpan[0] > 0f && fadeSpan[0] < 0.01f, "Fade-in first frame must start near zero");
            TestAssert.True(MathF.Abs(fadeSpan[^1] - 1.0f) < 1e-5f, "Fade-in 960th frame must reach unity");

            for (int f = 1; f < frames; f++)
            {
                float prev = fadeSpan[(f - 1) * Channels];
                float curr = fadeSpan[f * Channels];
                TestAssert.Equal(curr, fadeSpan[f * Channels + 1], "Fade-in must apply identical gain to L and R");
                TestAssert.True(curr >= prev, $"Fade-in envelope must be monotonically non-decreasing at frame {f}");
            }

            // Следующий фрейм завершает Fade-In с фиксацией ровно 1.0f
            bool fadeInDone = WinAudioBackend.ApplyFadeEnvelope(
                fadeSpan[..Channels],
                1,
                Channels,
                WinAudioBackend.FadeState.FadingIn,
                ref fadeGain);
            TestAssert.True(fadeInDone, "Fade-in must complete on terminal step");
            TestAssert.Equal(1.0f, fadeGain, "Fade-in terminal gain must be 1.0f");

            // 2. Математическая проверка Fade-Out и гарантированного зануления хвоста буфера
            fadeSpan.Fill(1.0f);
            fadeGain = 0.5f; // Должен завершиться ровно на половине буфера (480 фреймов), обнулив вторую половину

            bool fadeOutDone = WinAudioBackend.ApplyFadeEnvelope(
                fadeSpan,
                frames,
                Channels,
                WinAudioBackend.FadeState.FadingOut,
                ref fadeGain);

            TestAssert.True(fadeOutDone, "Fade-out from 0.5f must complete within 960 frames");
            TestAssert.Equal(0f, fadeGain, "Fade-out terminal gain must be 0f");
            for (int i = frames; i < sampleCount; i++)
            {
                TestAssert.Equal(0f, fadeSpan[i], $"Tail sample {i} after fade-out completion must be strictly 0f");
            }

            // 3. Инвариант 0 B аллокаций на нативном Span
            TestAssert.ZeroAlloc(() =>
            {
                ring.Write(sourceSamples.AsSpan());
                Span<float> wasapiSpan = new(nativeBuffer, sampleCount);
                int read = ring.Read(wasapiSpan);
                fadeGain = 0.5f;

                _ = WinAudioBackend.ApplyFadeEnvelope(
                    wasapiSpan,
                    read / Channels,
                    Channels,
                    WinAudioBackend.FadeState.FadingIn,
                    ref fadeGain);

                gainProcessor.Process(wasapiSpan);
            }, warmup: 5, iterations: 1000, context: "WASAPI Zero-Copy Native Span");
        }
        finally
        {
            NativeMemory.Free(nativeBuffer);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor Linear Scaling, Soft-Knee Ceiling & Ramp Continuity", Group = TestGroups.Audio, Order = 4)]
    public static Task TestGainProcessorLinearitySoftKneeAndRampAsync()
    {
        var processor = new GainProcessor();

        // 1. Побитовая линейность при стационарном коэффициенте <= 1.0f в пределах [-SafeCeiling, +SafeCeiling]
        processor.SetVolumeGain(0.5f);
        float[] warmup = new float[2400 * Channels];
        processor.Process(warmup.AsSpan()); // Завершаем рампу (1200 стерео-фреймов)

        float[] linearBuf = [-0.8f, -0.4f, 0.0f, 0.25f, 0.6f, 0.8f];
        float[] expectedBuf = [-0.4f, -0.2f, 0.0f, 0.125f, 0.3f, 0.4f];
        processor.Process(linearBuf.AsSpan());

        for (int i = 0; i < linearBuf.Length; i++)
        {
            TestAssert.Equal(
                BitConverter.SingleToInt32Bits(expectedBuf[i]),
                BitConverter.SingleToInt32Bits(linearBuf[i]),
                $"GainProcessor linear scaling mismatch at {i}");
        }

        // 2. Строгий асимптотический потолок SafeCeiling (0.985f) и монотонность при Volume Boost (> 1.0f)
        processor.SetVolumeGain(2.5f);
        processor.Process(warmup.AsSpan());

        float[] boostInputs = [0.1f, 0.3f, 0.36f, 0.5f, 0.8f, 1.0f]; // 0.36f * 2.5f = 0.90f (граница SoftKneeThreshold)
        float[] boostOutputs = (float[])boostInputs.Clone();
        processor.Process(boostOutputs.AsSpan());

        // До порога 0.90f сигнал масштабируется строго линейно
        TestAssert.True(MathF.Abs(boostOutputs[0] - 0.25f) < 1e-6f, "Sub-knee boost sample 0 must be linear");
        TestAssert.True(MathF.Abs(boostOutputs[1] - 0.75f) < 1e-6f, "Sub-knee boost sample 1 must be linear");
        TestAssert.True(MathF.Abs(boostOutputs[2] - 0.90f) < 1e-6f, "Knee boundary (0.90f) must be exact");

        for (int i = 1; i < boostOutputs.Length; i++)
        {
            TestAssert.True(boostOutputs[i] > boostOutputs[i - 1], "Soft-clip transfer function must be strictly monotonic");
            TestAssert.True(boostOutputs[i] <= 0.985f, $"Soft-clip output {boostOutputs[i]:F6} exceeded SafeCeiling (0.985f)");
        }

        // 3. Непрерывность рампы и идентичность L/R при перенацеливании громкости на лету
        processor.SetVolumeGain(0.2f);
        processor.Process(warmup.AsSpan());

        ReadOnlySpan<float> targets = [0.8f, 0.3f, 0.9f, 0.1f];
        float[] stereoChunk = new float[256];
        float prevSample = 0.2f;

        foreach (float target in targets)
        {
            Array.Fill(stereoChunk, 1.0f);
            processor.SetVolumeGain(target);
            processor.Process(stereoChunk.AsSpan());

            for (int i = 0; i < stereoChunk.Length; i += 2)
            {
                float left = stereoChunk[i];
                float right = stereoChunk[i + 1];
                TestAssert.Equal(left, right, $"Stereo imbalance during volume ramp at index {i}");

                // Максимальный теоретический шаг рампы за 1 фрейм при |Δgain| <= 0.8 равен 0.8 / 1200 = 0.00067f
                float step = MathF.Abs(left - prevSample);
                TestAssert.True(step <= 0.001f, $"Discontinuous volume step: {step:F6}");
                prevSample = left;
            }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "TruePeakLimiter & GainCrossfader Bit-Exact Pass & Stereo Link", Group = TestGroups.Audio, Order = 5)]
    public static Task TestTruePeakLimiterAndGainCrossfaderInvariantsAsync()
    {
        var limiter = new TruePeakLimiter(SampleRate);
        var crossfader = new GainCrossfader(1.0f);

        // 1. Побитовая прозрачность в линейной области (|x| <= KneeStart = 0.88f, gain = 1.0f)
        float[] linearSignal = new float[512 * Channels];
        for (int f = 0; f < 512; f++)
        {
            float s = MathF.Sin(2f * MathF.PI * 440f * f / SampleRate) * 0.85f;
            linearSignal[f * 2] = s;
            linearSignal[f * 2 + 1] = -s;
        }

        float[] working = (float[])linearSignal.Clone();
        limiter.Process(working.AsSpan(), ref crossfader);

        for (int i = 0; i < working.Length; i++)
        {
            TestAssert.Equal(
                BitConverter.SingleToInt32Bits(linearSignal[i]),
                BitConverter.SingleToInt32Bits(working[i]),
                $"TruePeakLimiter altered linear sample below KneeStart (0.88f) at index {i}");
        }

        // 2. Строгий Stereo Link: если пик возникает только в левом канале (L = 1.6f, R = 0.4f -> отношение 4:1),
        // оба канала обязаны ослабляться на идентичный коэффициент с сохранением отношения L/R == 4:1 и потолка <= 0.985f.
        limiter.Reset();
        crossfader.Reset(1.0f);

        float[] asymmetricStereo = new float[512 * Channels];
        for (int f = 0; f < 512; f++)
        {
            asymmetricStereo[f * 2] = 1.6f;
            asymmetricStereo[f * 2 + 1] = 0.4f;
        }

        TestAssert.ZeroAlloc(() =>
        {
            asymmetricStereo.AsSpan().CopyTo(working.AsSpan());
            limiter.Process(working.AsSpan(), ref crossfader);
        }, warmup: 3, iterations: 500, context: "TruePeakLimiter.Process HotPath");

        for (int f = 0; f < 512; f++)
        {
            float outL = working[f * 2];
            float outR = working[f * 2 + 1];
            TestAssert.True(outL <= 0.985f && outR <= 0.985f, $"Limiter ceiling breached at frame {f}: L={outL:F5}");
            TestAssert.True(outR > 0f, "Right channel must not be zeroed out");

            // Проверяем, что отношение L/R не разрушено независимым клиппингом одного канала
            float ratio = outL / outR;
            TestAssert.True(ratio > 2.0f && ratio <= 4.001f, $"Stereo image collapsed at frame {f}: L/R={ratio:F3}");
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Equalizer 10-Band Bypass Identity, Selectivity & SeqLock Concurrency", Group = TestGroups.Audio, Order = 6)]
    public static async Task TestEqualizerBypassFrequencySelectivityAndConcurrencyAsync()
    {
        var eq = new EqualizerProcessor(SampleRate, Channels);

        // 1. Побитовая прозрачность в режиме Bypass
        float[] input1k = new float[2048 * Channels];
        for (int f = 0; f < 2048; f++)
        {
            float s = MathF.Sin(2f * MathF.PI * 1000f * f / SampleRate) * 0.25f;
            input1k[f * 2] = s;
            input1k[f * 2 + 1] = s;
        }

        float[] working = (float[])input1k.Clone();
        eq.Process(working.AsSpan());
        TestAssert.True(eq.IsBypassed, "Default EqualizerProcessor must be in bypass");
        for (int i = 0; i < working.Length; i++)
        {
            TestAssert.Equal(
                BitConverter.SingleToInt32Bits(input1k[i]),
                BitConverter.SingleToInt32Bits(working[i]),
                $"Bypassed EQ modified sample at {i}");
        }

        // 2. Точность коэффициента усиления Cytomic SVF на центральной частоте полосы 1 кГц (+12 dB и -12 dB)
        Span<float> bands = stackalloc float[EqualizerProcessor.BandCount];
        bands.Clear();
        bands[5] = 12.0f; // 1000 Гц -> +12 dB (3.98107x)

        eq.Configure(enabled: true, bands, preampDb: 0f, autoPreamp: false);
        eq.SnapToTarget();

        for (int pass = 0; pass < 3; pass++)
        {
            input1k.AsSpan().CopyTo(working.AsSpan());
            eq.Process(working.AsSpan());
        }

        float boostPeak = 0f;
        for (int i = working.Length / 2; i < working.Length; i += 2)
        {
            TestAssert.Equal(working[i], working[i + 1], "EQ stereo mismatch");
            float abs = MathF.Abs(working[i]);
            if (abs > boostPeak) boostPeak = abs;
        }

        float expectedBoostPeak = 0.25f * MathF.Pow(10f, 12.0f / 20.0f);
        TestAssert.True(
            MathF.Abs(boostPeak - expectedBoostPeak) < 0.04f,
            $"1 kHz +12 dB gain mismatch: actual={boostPeak:F4}, expected={expectedBoostPeak:F4}");

        // Ослабление -12 dB (0.25119x) на той же полосе 1 кГц
        eq.Reset();
        bands[5] = -12.0f;
        eq.Configure(enabled: true, bands, preampDb: 0f, autoPreamp: false);
        eq.SnapToTarget();

        for (int pass = 0; pass < 3; pass++)
        {
            input1k.AsSpan().CopyTo(working.AsSpan());
            eq.Process(working.AsSpan());
        }

        float cutPeak = 0f;
        for (int i = working.Length / 2; i < working.Length; i += 2)
        {
            float abs = MathF.Abs(working[i]);
            if (abs > cutPeak) cutPeak = abs;
        }

        float expectedCutPeak = 0.25f * MathF.Pow(10f, -12.0f / 20.0f);
        TestAssert.True(
            MathF.Abs(cutPeak - expectedCutPeak) < 0.015f,
            $"1 kHz -12 dB cut mismatch: actual={cutPeak:F4}, expected={expectedCutPeak:F4}");

        // 3. Приоритет разрешения пресетов (ForceGlobalEqualizer vs Per-Track) и биекция 5 <-> 10 полос
        var settings = new AudioSettings
        {
            EqualizerEnabled = true,
            ForceGlobalEqualizer = false,
            GlobalEqualizerPresetId = EqualizerPresets.RockId
        };
        var track = new TrackInfo { Id = "yt_test", EqualizerPresetId = EqualizerPresets.VocalId };

        Span<float> resolved = stackalloc float[EqualizerProcessor.BandCount];
        TestAssert.Equal(EqualizerPresets.VocalId, EqualizerPresets.ResolveEffectiveBands(settings, track, resolved));

        settings.ForceGlobalEqualizer = true;
        TestAssert.Equal(EqualizerPresets.RockId, EqualizerPresets.ResolveEffectiveBands(settings, track, resolved));

        ReadOnlySpan<float> macro5In = [4.5f, -3.0f, 2.0f, -6.5f, 8.0f];
        Span<float> iso10 = stackalloc float[EqualizerProcessor.BandCount];
        Span<float> macro5Out = stackalloc float[5];
        EqualizerPresets.MapMacro5ToIso10(macro5In, iso10);
        EqualizerPresets.MapIso10ToMacro5(iso10, macro5Out);
        for (int i = 0; i < 5; i++)
            TestAssert.Equal(macro5In[i], macro5Out[i], $"Macro5 <-> Iso10 roundtrip failed at band {i}");

        // 4. Стресс-тест конкурентной публикации SeqLock (Configure из фоновых потоков vs Process за 0 B аллокаций)
        TestAssert.ZeroAlloc(() =>
        {
            input1k.AsSpan(0, 512).CopyTo(working.AsSpan(0, 512));
            eq.Process(working.AsSpan(0, 512));
        }, warmup: 5, iterations: 500, context: "EqualizerProcessor.Process HotPath");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var token = cts.Token;
        var writerTask = Task.Run(() =>
        {
            int step = 0;
            while (!token.IsCancellationRequested)
            {
                var preset = (step++ & 1) == 0
                    ? EqualizerPresets.GetPresetBands(EqualizerPresets.BassBoostId)
                    : EqualizerPresets.GetPresetBands(EqualizerPresets.VocalId);
                eq.Configure(enabled: (step % 4) != 0, preset);
            }
        }, token);

        float[] audioChunk = new float[256 * Channels];
        while (!token.IsCancellationRequested)
        {
            Array.Fill(audioChunk, 0.25f);
            eq.Process(audioChunk.AsSpan());
            for (int i = 0; i < audioChunk.Length; i++)
                TestAssert.True(float.IsFinite(audioChunk[i]) && MathF.Abs(audioChunk[i]) < 10f, "SeqLock race corrupted EQ state");
        }

        await writerTask.ConfigureAwait(false);
    }

    [TestMethod(TestCategory.Unit, "AudioSpectrumAnalyzer Band Isolation & Visualizer Buffer (0 Alloc)", Group = TestGroups.Audio, Order = 7)]
    public static Task TestAudioSpectrumAnalyzerBandIsolationAndVisualizerBufferAsync()
    {
        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        float[] signal = new float[2048 * Channels];
        Span<float> bands10 = stackalloc float[EqualizerProcessor.BandCount];

        void FeedFrequency(float freqHz)
        {
            analyzer.Reset();
            for (int f = 0; f < 2048; f++)
            {
                float s = MathF.Sin(2f * MathF.PI * freqHz * f / SampleRate) * 0.5f;
                signal[f * 2] = s;
                signal[f * 2 + 1] = s;
            }
            for (int pass = 0; pass < 4; pass++)
                analyzer.ProcessBlock(signal.AsSpan(), Channels);
        }

        // 1. НЧ сигнал 62.5 Гц (полоса 1) должен доминировать в LowLevel
        FeedFrequency(62.5f);
        AudioVisualizerBuffer.CopyBands10(bands10);
        TestAssert.True(AudioVisualizerBuffer.LowLevel > AudioVisualizerBuffer.MidLevel * 2.0f, "62.5Hz must dominate LowLevel");
        TestAssert.True(AudioVisualizerBuffer.LowLevel > AudioVisualizerBuffer.HighLevel * 3.0f, "62.5Hz must not leak into HighLevel");
        TestAssert.True(bands10[1] > bands10[5] * 2.5f, "Band[1] (62Hz) must exceed Band[5] (1kHz)");

        // 2. СЧ сигнал 1000 Гц (полоса 5) должен доминировать в MidLevel и выдавать DominantBandIndex == 5
        FeedFrequency(1000.0f);
        AudioVisualizerBuffer.CopyBands10(bands10);
        TestAssert.Equal(5, AudioVisualizerBuffer.DominantBandIndex);
        TestAssert.True(AudioVisualizerBuffer.MidLevel > AudioVisualizerBuffer.LowLevel * 2.0f, "1kHz must dominate MidLevel");

        // 3. ВЧ сигнал 8000 Гц (полоса 8) должен доминировать в HighLevel
        FeedFrequency(8000.0f);
        AudioVisualizerBuffer.CopyBands10(bands10);
        TestAssert.True(AudioVisualizerBuffer.HighLevel > AudioVisualizerBuffer.LowLevel * 3.0f, "8kHz must dominate HighLevel");
        TestAssert.True(bands10[8] > bands10[1] * 3.0f, "Band[8] (8kHz) must exceed Band[1] (62Hz)");

        // 4. Zero-Alloc (после 50 итераций прогрева для завершения Tiered PGO компиляции) и чистота Reset()
        TestAssert.ZeroAlloc(
            () => analyzer.ProcessBlock(signal.AsSpan(), Channels),
            warmup: 50,
            iterations: 500,
            context: "AudioSpectrumAnalyzer.ProcessBlock");

        analyzer.Reset();
        TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Reset must clear HasRecentActivity");
        TestAssert.Equal(0f, AudioVisualizerBuffer.OverallLevel);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "EBU R128 / ITU-R BS.1770-4 Reference & Pre-Scan Config Invariants (0 Alloc)", Group = TestGroups.Audio, Order = 8)]
    public static Task TestEbuR128AnalyzerAndKWeightingMathematicalReferenceAsync()
    {
        const int blockFrames = 4800; // 100 мс @ 48 кГц
        const int blockSamples = blockFrames * Channels;

        // 1. Эталонная проверка ITU-R BS.1770-4: стерео-синус 997 Гц с пиковой амплитудой 1.0
        var kFilter = new KWeightingFilter(SampleRate, Channels);
        float[] sine997 = new float[blockSamples];
        double[] kOut = new double[blockSamples];
        double[] gatingBlocks = new double[4];

        for (int f = 0; f < blockFrames; f++)
        {
            float s = MathF.Sin(2f * MathF.PI * 997f * f / SampleRate);
            sine997[f * 2] = s;
            sine997[f * 2 + 1] = s;
        }

        // Устанавливаем стационарный режим IIR-фильтров K-weighting
        kFilter.ProcessBlock(sine997.AsSpan(), kOut.AsSpan());

        double power = 0.0;
        TestAssert.ZeroAlloc(() =>
        {
            kFilter.ProcessBlock(sine997.AsSpan(), kOut.AsSpan());
            double sumSq = 0.0;
            for (int i = 0; i < blockSamples; i++)
                sumSq += kOut[i] * kOut[i];
            power = sumSq / blockFrames;
        }, warmup: 40, iterations: 100, context: "KWeightingFilter.ProcessBlock");

        gatingBlocks[0] = power;
        gatingBlocks[1] = power;
        gatingBlocks[2] = power;
        gatingBlocks[3] = 1e-7; // Блок ниже относительного порога (-10 LU), обязан быть отброшен гейтом

        float lufs = 0f;
        float gain = 0f;
        TestAssert.ZeroAlloc(() =>
        {
            lufs = EbuR128Analyzer.ComputeIntegratedLufsFromBlocks(gatingBlocks, 4);
            gain = EbuR128Analyzer.ComputeGainFromLufs(lufs, -14.0f, 3.0f);
        }, warmup: 40, iterations: 500, context: "EbuR128Analyzer Gating & Gain Computation");

        // По стандарту ITU-R BS.1770-4 константа -0.691 dB компенсирует +0.653 dB подъём K-фильтра на 997 Гц,
        // поэтому двухканальный синус с амплитудой 1.0 даёт ~0.0 LUFS (-0.038 LUFS)
        TestAssert.True(
            MathF.Abs(lufs - (-0.038f)) < 0.02f,
            $"ITU-R BS.1770-4 reference sine LUFS out of tolerance: {lufs:F3} LUFS");
        TestAssert.Equal(
            gain,
            EbuR128Analyzer.ComputeIntegratedGainFromBlocks(gatingBlocks, 4, -14.0f, 3.0f));

        // 2. Проверка немедленного применения конфигурации до первого вызова ProcessSamples() (Pre-Scan сценарий)
        var analyzer = new EbuR128Analyzer();
        var config = new NormalizationConfig(
            Enabled: true,
            TargetLufs: -18.0f,
            MaxGain: 1.5f,
            Mode: NormalizationMode.DownwardOnly,
            PreScanDurationMs: 45_000);

        analyzer.Configure(config);

        var current = analyzer.CurrentConfig;
        TestAssert.Equal(-18.0f, current.TargetLufs);
        TestAssert.Equal(1.5f, current.MaxGain);
        TestAssert.Equal(NormalizationMode.DownwardOnly, current.Mode);
        TestAssert.Equal(45_000, current.PreScanDurationMs);

        // В режиме DownwardOnly любой gain > 1.0f при LockGain обязан ограничиваться сверху 1.0f
        analyzer.LockGain(2.2f);
        TestAssert.Equal(1.0f, analyzer.GetLockedGain());
        TestAssert.Equal(1.0f, analyzer.ProcessSamples());

        // 3. Динамический пересчёт Gain при изменении настроек (Bidirectional vs DownwardOnly) без повторного сканирования
        float? callbackLufs = null;
        analyzer.SetIntegratedLufsCallback(val => callbackLufs = val);
        analyzer.NotifyIntegratedLufs(-20.0f); // На 6 LU тише цели -14 LUFS (~1.995x усиление)
        TestAssert.Equal(-20.0f, callbackLufs);

        analyzer.Configure(new NormalizationConfig(
            Enabled: true,
            TargetLufs: -14.0f,
            MaxGain: 3.0f,
            Mode: NormalizationMode.Bidirectional));

        float recalculated = analyzer.ProcessSamples();
        float expectedBidirectional = MathF.Pow(10f, (-14.0f - (-20.0f)) / 20f);
        TestAssert.True(
            MathF.Abs(recalculated - expectedBidirectional) < 0.001f,
            $"Dynamic recalculation mismatch: actual={recalculated:F4}, expected={expectedBidirectional:F4}");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "PlaybackErrorBehaviorMatrix & AudioPlayer Timer Lifecycle", Group = TestGroups.Audio, Order = 9)]
    public static async Task TestPlaybackErrorMatrixAndTimerLifecycleAsync()
    {
        // 1. Проверка матрицы политик восстановления после ошибок воспроизведения
        var dialogPolicy = UI.Helpers.PlaybackErrorBehaviorMatrix.Resolve(
            PlaybackErrorBehavior.Dialog, PlaybackFailureBehavior.SkipAndPlay);
        TestAssert.True(dialogPolicy.ShowNotification);
        TestAssert.True(dialogPolicy.RequiresUserAction);
        TestAssert.Equal(UI.Helpers.PlaybackRecoveryMode.AwaitUserAction, dialogPolicy.RecoveryMode);

        var toastSkipPlay = UI.Helpers.PlaybackErrorBehaviorMatrix.Resolve(
            PlaybackErrorBehavior.ToastAndSkip, PlaybackFailureBehavior.SkipAndPlay);
        TestAssert.True(toastSkipPlay.ShowNotification);
        TestAssert.False(toastSkipPlay.RequiresUserAction);
        TestAssert.Equal(UI.Helpers.PlaybackRecoveryMode.SkipAndPlay, toastSkipPlay.RecoveryMode);

        var ignoreStop = UI.Helpers.PlaybackErrorBehaviorMatrix.Resolve(
            PlaybackErrorBehavior.Ignore, PlaybackFailureBehavior.Stop);
        TestAssert.False(ignoreStop.ShowNotification, "Ignore mode must suppress notification toasts");
        TestAssert.Equal(UI.Helpers.PlaybackRecoveryMode.Stop, ignoreStop.RecoveryMode);

        // 2. Проверка сохранения активности _bufferTimer в AudioPlayer при переходах Buffering -> Suspend -> Resume -> StartPositionTimerDelayed
        await using var player = new AudioPlayer(new AudioPlayerOptions
        {
            UseNullBackend = true,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(25)
        });

        var stateField = typeof(AudioPlayer).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var startPosDelayedMethod = typeof(AudioPlayer).GetMethod("StartPositionTimerDelayed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var bufferTimerField = typeof(AudioPlayer).GetField("_bufferTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Переводим плеер в состояние Buffering (как при Starvation) и проверяем, что StartPositionTimerDelayed запускает _bufferTimer
        stateField.SetValue(player, PlayerState.Buffering);
        player.SetUiTimersSuspended(true);
        player.SetUiTimersSuspended(false);

        startPosDelayedMethod.Invoke(player, null);
        var bufferTimer = (Timer?)bufferTimerField.GetValue(player);
        TestAssert.NotNull(bufferTimer, "_bufferTimer must be initialized and active when StartPositionTimerDelayed is called in Buffering/Playing state");
    }
}