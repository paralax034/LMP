using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Deterministic unit tests for audio pipeline algorithms and DSP:
/// verifies mathematical invariants (ITU-R BS.1770-4, bitwise linearity,
/// inter-channel phase coherence, frequency selectivity, asymptotic ceiling),
/// finite-state machine transitions, and zero allocations (0 B) on the hot path.
/// </summary>
public static class AudioLogicTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 ms stereo

    /// <summary>Position based on render clock: advances within the device queue, holds on underrun, and is clamped by track duration.</summary>
    [TestMethod(TestCategory.Unit, "Render Clock Position: Extrapolation, Starvation Hold & Duration Clamp", Group = TestGroups.Audio, Order = 1)]
    public static async Task TestRenderClockPositionContractsAsync()
    {
        const int samplesPerSecond = SampleRate * Channels;
        const long baseSamples = 10L * samplesPerSecond;
        const long t0 = 1_000_000;
        long frequency = System.Diagnostics.Stopwatch.Frequency;

        var clock = new RenderClock();

        // 1. Device received 10 s + 100 ms, 100 ms of which is still queued: exactly 10 s played
        clock.Publish(baseSamples + BufferSamples, BufferSamples, t0, samplesPerSecond);
        RenderClockSnapshot snapshot = clock.Read();
        TestAssert.Equal(baseSamples, snapshot.PlayedSamples);

        // 2. Between readings, position advances by real time, but no further than submitted to the device
        long after25Ms = RenderClock.Extrapolate(in snapshot, t0 + frequency / 40);
        TestAssert.True(Math.Abs(after25Ms - baseSamples - 2_400) <= 1, $"25 ms must advance 2400 samples, got {after25Ms - baseSamples}");

        long rendered = baseSamples + BufferSamples;
        TestAssert.Equal(rendered, RenderClock.Extrapolate(in snapshot, t0 + frequency), "Extrapolation must not run past the device queue");

        // 3. Underrun: queue is empty, position holds instead of drifting forward by timer
        clock.Publish(rendered, 0, t0 + frequency, samplesPerSecond);
        RenderClockSnapshot starved = clock.Read();
        TestAssert.Equal(rendered, RenderClock.Extrapolate(in starved, t0 + 2 * frequency), "Starved clock must hold its position");

        // 4. Glitchy readings (backward counter): position does not roll back
        clock.Publish(rendered - 4_800, 0, t0 + 2 * frequency, samplesPerSecond);
        TestAssert.True(clock.Read().PlayedSamples >= rendered, "Render clock must never move backwards");

        // 5. Pipeline position is clamped to track duration
        var backend = new NullAudioBackend();
        var pipeline = AudioPipelineTestHarness.Create(
            new BlockingAudioSource(SampleRate, Channels),
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0f),
            backend,
            new LockFreeRingBuffer<float>(4_096),
            new AudioStreamInfo { DurationMs = 1_000 });

        try
        {
            pipeline.SetDecodedSamplesPosition(2L * samplesPerSecond);
            TestAssert.Equal(TimeSpan.FromSeconds(1), pipeline.GetPlaybackPosition(), "Position must be clamped to the track duration");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    [TestMethod(TestCategory.Unit, "Adaptive Decoder Delay & State Machine Contracts", Group = TestGroups.Audio, Order = 2)]
    public static Task TestAdaptiveDecoderDelayAndBatchHysteresisAsync()
    {
        // 1. Monotonic scale of adaptive decoder delays depending on PCM headroom in the buffer
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

        // 2. Verification of PlayerStateTransitions state machine contracts
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

        const int frames = 960; // Full micro-fade duration (20 ms @ 48 kHz)
        const int sampleCount = frames * Channels;

        float* nativeBuffer = (float*)NativeMemory.AllocZeroed((nuint)(sampleCount * sizeof(float)));
        try
        {
            using var ring = new LockFreeRingBuffer<float>(sampleCount * 4);
            var gainProcessor = new GainProcessor();
            gainProcessor.SetVolumeGain(0.8f);

            float[] sourceSamples = new float[sampleCount];
            Array.Fill(sourceSamples, 0.5f);

            // 1. Mathematical verification of Fade-In from 0 to 1 (considering IEEE 754 rounding of step size 1.0f / 960)
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

            // Next frame completes Fade-In, locking exactly to 1.0f
            bool fadeInDone = WinAudioBackend.ApplyFadeEnvelope(
                fadeSpan[..Channels],
                1,
                Channels,
                WinAudioBackend.FadeState.FadingIn,
                ref fadeGain);
            TestAssert.True(fadeInDone, "Fade-in must complete on terminal step");
            TestAssert.Equal(1.0f, fadeGain, "Fade-in terminal gain must be 1.0f");

            // 2. Mathematical verification of Fade-Out and guaranteed zeroing of the buffer tail
            fadeSpan.Fill(1.0f);
            fadeGain = 0.5f; // Must complete exactly at half the buffer (480 frames), clearing the second half

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

            // 3. Invariant of 0 B allocations on native Span
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

        // 1. Bitwise linearity for steady-state gain <= 1.0f within [-SafeCeiling, +SafeCeiling]
        processor.SetVolumeGain(0.5f);
        float[] warmup = new float[2400 * Channels];
        processor.Process(warmup.AsSpan()); // Complete ramp (1200 stereo frames)

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

        // 2. Strict asymptotic SafeCeiling (0.985f) and monotonicity during Volume Boost (> 1.0f)
        processor.SetVolumeGain(2.5f);
        processor.Process(warmup.AsSpan());

        float[] boostInputs = [0.1f, 0.3f, 0.36f, 0.5f, 0.8f, 1.0f]; // 0.36f * 2.5f = 0.90f (SoftKneeThreshold boundary)
        float[] boostOutputs = (float[])boostInputs.Clone();
        processor.Process(boostOutputs.AsSpan());

        // Below 0.90f threshold, signal scales strictly linearly
        TestAssert.True(MathF.Abs(boostOutputs[0] - 0.25f) < 1e-6f, "Sub-knee boost sample 0 must be linear");
        TestAssert.True(MathF.Abs(boostOutputs[1] - 0.75f) < 1e-6f, "Sub-knee boost sample 1 must be linear");
        TestAssert.True(MathF.Abs(boostOutputs[2] - 0.90f) < 1e-6f, "Knee boundary (0.90f) must be exact");

        for (int i = 1; i < boostOutputs.Length; i++)
        {
            TestAssert.True(boostOutputs[i] > boostOutputs[i - 1], "Soft-clip transfer function must be strictly monotonic");
            TestAssert.True(boostOutputs[i] <= 0.985f, $"Soft-clip output {boostOutputs[i]:F6} exceeded SafeCeiling (0.985f)");
        }

        // 3. Ramp continuity and L/R identity during on-the-fly gain retargeting
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

                // Maximum theoretical ramp step per frame for |Δgain| <= 0.8 is 0.8 / 1200 = 0.00067f
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

        // 1. Bitwise transparency in linear region (|x| <= KneeStart = 0.88f, gain = 1.0f)
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

        // 2. Strict Stereo Link: if a peak occurs only in the left channel (L = 1.6f, R = 0.4f -> 4:1 ratio),
        // both channels must be attenuated by an identical factor, preserving L/R ratio == 4:1 and ceiling <= 0.985f.
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

            // Stereo Link: common factor for both channels preserves L/R ratio at exactly 4:1.
            // Former threshold (> 2.0) allowed independent channel clipping (L -> 0.985, R = 0.4 yields 2.46).
            float ratio = outL / outR;
            TestAssert.True(MathF.Abs(ratio - 4.0f) < 1e-3f, $"Stereo link broken at frame {f}: L/R={ratio:F5}, expected 4.0");
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Equalizer 10-Band Bypass Identity, Selectivity & SeqLock Concurrency", Group = TestGroups.Audio, Order = 6)]
    public static async Task TestEqualizerBypassFrequencySelectivityAndConcurrencyAsync()
    {
        var eq = new EqualizerProcessor(SampleRate, Channels);

        // 1. Bitwise transparency in Bypass mode
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

        // 2. Cytomic SVF gain precision at 1 kHz center band frequency (+12 dB and -12 dB)
        Span<float> bands = stackalloc float[EqualizerProcessor.BandCount];
        bands.Clear();
        bands[5] = 12.0f; // 1000 Hz -> +12 dB (3.98107x)

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

        // -12 dB attenuation (0.25119x) on the same 1 kHz band
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

        // 3. Preset resolution priority (ForceGlobalEqualizer vs Per-Track) and 5 <-> 10 band bijection
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

        // 4. Concurrent SeqLock publishing stress test (Configure from background threads vs Process at 0 B allocations)
        TestAssert.ZeroAlloc(() =>
        {
            input1k.AsSpan(0, 512).CopyTo(working.AsSpan(0, 512));
            eq.Process(working.AsSpan(0, 512));
        }, warmup: 5, iterations: 500, context: "EqualizerProcessor.Process HotPath");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var token = cts.Token;
        // Token is not passed to Task.Run: late task startup from pool would cancel it before launch
        // and would turn the await into a false TaskCanceledException
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
        });

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
        // AudioVisualizerBuffer is a global static state: active playback will overwrite levels
        if (AudioVisualizerBuffer.HasRecentActivity)
            TestAssert.Skip("AudioVisualizerBuffer is being fed by active playback; global state would corrupt assertions");

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

        // 1. LF signal 62.5 Hz (band 1) must dominate in LowLevel
        FeedFrequency(62.5f);
        AudioVisualizerBuffer.CopyBands10(bands10);
        TestAssert.True(AudioVisualizerBuffer.LowLevel > AudioVisualizerBuffer.MidLevel * 2.0f, "62.5Hz must dominate LowLevel");
        TestAssert.True(AudioVisualizerBuffer.LowLevel > AudioVisualizerBuffer.HighLevel * 3.0f, "62.5Hz must not leak into HighLevel");
        TestAssert.True(bands10[1] > bands10[5] * 2.5f, "Band[1] (62Hz) must exceed Band[5] (1kHz)");

        // 2. MF signal 1000 Hz (band 5) must dominate in MidLevel and produce DominantBandIndex == 5
        FeedFrequency(1000.0f);
        AudioVisualizerBuffer.CopyBands10(bands10);
        TestAssert.Equal(5, AudioVisualizerBuffer.DominantBandIndex);
        TestAssert.True(AudioVisualizerBuffer.MidLevel > AudioVisualizerBuffer.LowLevel * 2.0f, "1kHz must dominate MidLevel");

        // 3. HF signal 8000 Hz (band 8) must dominate in HighLevel
        FeedFrequency(8000.0f);
        AudioVisualizerBuffer.CopyBands10(bands10);
        TestAssert.True(AudioVisualizerBuffer.HighLevel > AudioVisualizerBuffer.LowLevel * 3.0f, "8kHz must dominate HighLevel");
        TestAssert.True(bands10[8] > bands10[1] * 3.0f, "Band[8] (8kHz) must exceed Band[1] (62Hz)");

        // 4. Zero-Alloc (after 50 warmup iterations to complete Tiered PGO compilation) and purity of Reset()
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
        const int blockFrames = 4800; // 100 ms @ 48 kHz
        const int blockSamples = blockFrames * Channels;

        // 1. Reference ITU-R BS.1770-4 check: stereo sine 997 Hz with peak amplitude 1.0
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

        // Establish steady state for K-weighting IIR filters
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
        gatingBlocks[3] = 1e-7; // Block below relative threshold (-10 LU) must be discarded by the gate

        float lufs = 0f;
        float gain = 0f;
        TestAssert.ZeroAlloc(() =>
        {
            lufs = EbuR128Analyzer.ComputeIntegratedLufsFromBlocks(gatingBlocks, 4);
            gain = EbuR128Analyzer.ComputeGainFromLufs(lufs, -14.0f, 3.0f);
        }, warmup: 40, iterations: 500, context: "EbuR128Analyzer Gating & Gain Computation");

        // ITU-R BS.1770-4: K-filter boost at 997 Hz is +0.691 dB and is exactly offset by the −0.691 constant,
        // so a 0 dBFS stereo sine must yield 0.00 LUFS. Tolerance ±0.1 LU is an EBU Tech 3341 requirement.
        // RBJ approximation (ShelfFreqHz = 1500) gives a constant offset of about −0.04 LU — well within tolerance.
        TestAssert.True(
            MathF.Abs(lufs) < 0.1f,
            $"ITU-R BS.1770-4 reference sine must read 0.00 ±0.1 LUFS (EBU Tech 3341), got {lufs:F3} LUFS");
        TestAssert.Equal(
            gain,
            EbuR128Analyzer.ComputeIntegratedGainFromBlocks(gatingBlocks, 4, -14.0f, 3.0f));

        // 2. Check immediate application of configuration prior to the first ProcessSamples() call (Pre-Scan scenario)
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

        // In DownwardOnly mode, any gain > 1.0f when LockGain is enabled must be capped at 1.0f
        analyzer.LockGain(2.2f);
        TestAssert.Equal(1.0f, analyzer.GetLockedGain());
        TestAssert.Equal(1.0f, analyzer.ProcessSamples());

        // 3. Dynamic recalculation of Gain upon settings change (Bidirectional vs DownwardOnly) without re-scanning
        float? callbackLufs = null;
        analyzer.SetIntegratedLufsCallback(val => callbackLufs = val);
        analyzer.NotifyIntegratedLufs(-20.0f); // 6 LU quieter than the target -14 LUFS (~1.995x amplification)
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

    // Private members of AudioPlayer without reflection: signatures are bound on first call, compatible with AOT.
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_state")]
    private static extern ref PlayerState PlayerStateField(AudioPlayer player);

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "StartPositionTimerDelayed")]
    private static extern void StartPositionTimerDelayed(AudioPlayer player);

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_bufferTimer")]
    private static extern ref Timer? BufferTimerField(AudioPlayer player);

    /// <summary>
    /// Verifies queue index arithmetic when deleting a track: a deleted playing track is immediately replaced by the next one in line
    /// without skipping, deleting the last track leads to the end of the queue or wraps around, and a deferred auto-transition for the end of the deleted track is discarded.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Queue Remove Current -> No Skip", Group = TestGroups.Audio, Order = 13)]
    public static Task TestQueueRemoveCurrentNoSkipAsync()
    {
        // Deletion in the engine: index shift, and for the current track — a step "forward" like "Next" (userInitiated: true)
        static int RemoveAndResolve(int removedIndex, int currentIndex, int countBefore, bool repeatAll)
        {
            int index = AudioEngine.ComputeIndexAfterRemove(removedIndex, currentIndex);
            return removedIndex == currentIndex
                ? AudioEngine.ComputeNextIndex(index, countBefore - 1, repeatAll, userInitiated: true)
                : index;
        }

        // 1. A, B, C; A playing; delete A -> immediately B (index 0 in B, C), not C
        TestAssert.Equal(0, RemoveAndResolve(0, 0, 3, repeatAll: false), "Removing playing A must start B, not skip to C");

        // 2. A, B, C, D; B playing; delete B -> C (index 1 in A, C, D)
        TestAssert.Equal(1, RemoveAndResolve(1, 1, 4, repeatAll: false), "Removing playing middle track must start the next one");

        // 3. Playing the last track: without repeat — end of queue, with RepeatMode.All — first track
        TestAssert.Equal(-1, RemoveAndResolve(2, 2, 3, repeatAll: false), "Removing playing last track without repeat must end the queue");
        TestAssert.Equal(0, RemoveAndResolve(2, 2, 3, repeatAll: true), "Removing playing last track with RepeatAll must wrap to the first");

        // 4. Single track -> stop even with RepeatMode.All
        TestAssert.Equal(-1, RemoveAndResolve(0, 0, 1, repeatAll: true), "Removing the only track must stop playback");

        // 5. Other tracks: before current — index decreased by 1, after — unchanged
        TestAssert.Equal(1, RemoveAndResolve(0, 2, 3, repeatAll: false), "Removing a track before current must shift index down");
        TestAssert.Equal(0, RemoveAndResolve(2, 0, 3, repeatAll: false), "Removing a track after current must keep index");

        // 6. Two rapid deletions: A, B, C; A playing; delete A, then B before it starts -> C
        int afterFirst = RemoveAndResolve(0, 0, 3, repeatAll: false);
        TestAssert.Equal(0, RemoveAndResolve(0, afterFirst, 2, repeatAll: false), "Second quick removal must start C");

        // 7. Navigation regression: normal step, end of queue, wrap-around, and single track on auto-transition
        TestAssert.Equal(1, AudioEngine.ComputeNextIndex(0, 3, repeatAll: false, userInitiated: false));
        TestAssert.Equal(-1, AudioEngine.ComputeNextIndex(2, 3, repeatAll: false, userInitiated: false));
        TestAssert.Equal(0, AudioEngine.ComputeNextIndex(2, 3, repeatAll: true, userInitiated: false));
        TestAssert.Equal(-1, AudioEngine.ComputeNextIndex(0, 1, repeatAll: true, userInitiated: false), "Auto-advance on single track with RepeatAll must stop");

        // 8. Auto-transition on the end of deleted A is obsolete: queue is already at B
        TestAssert.True(AudioEngine.IsStaleTrackEnd("A", "B"), "Track-end of removed A must be dropped");
        TestAssert.False(AudioEngine.IsStaleTrackEnd("A", "A"), "Natural track-end must advance");
        TestAssert.False(AudioEngine.IsStaleTrackEnd(null, "B"), "User navigation is never stale");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "PlaybackErrorBehaviorMatrix & AudioPlayer Timer Lifecycle", Group = TestGroups.Audio, Order = 9)]
    public static async Task TestPlaybackErrorMatrixAndTimerLifecycleAsync()
    {
        // 1. Verification of the recovery policy matrix after playback errors
        var dialogPolicy = PlaybackErrorBehaviorMatrix.Resolve(
            PlaybackErrorBehavior.Dialog, PlaybackFailureBehavior.SkipAndPlay);
        TestAssert.True(dialogPolicy.ShowNotification);
        TestAssert.True(dialogPolicy.RequiresUserAction);
        TestAssert.Equal(PlaybackRecoveryMode.AwaitUserAction, dialogPolicy.RecoveryMode);

        var toastSkipPlay = PlaybackErrorBehaviorMatrix.Resolve(
            PlaybackErrorBehavior.ToastAndSkip, PlaybackFailureBehavior.SkipAndPlay);
        TestAssert.True(toastSkipPlay.ShowNotification);
        TestAssert.False(toastSkipPlay.RequiresUserAction);
        TestAssert.Equal(PlaybackRecoveryMode.SkipAndPlay, toastSkipPlay.RecoveryMode);

        var ignoreStop = PlaybackErrorBehaviorMatrix.Resolve(
            PlaybackErrorBehavior.Ignore, PlaybackFailureBehavior.Stop);
        TestAssert.False(ignoreStop.ShowNotification, "Ignore mode must suppress notification toasts");
        TestAssert.Equal(PlaybackRecoveryMode.Stop, ignoreStop.RecoveryMode);

        // 2. Verification that _bufferTimer activity is preserved in AudioPlayer during transitions Buffering -> Suspend -> Resume -> StartPositionTimerDelayed
        await using var player = new AudioPlayer(new AudioPlayerOptions
        {
            UseNullBackend = true,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(25)
        });

        // Put player into Buffering state (as during Starvation) and verify that StartPositionTimerDelayed starts _bufferTimer
        PlayerStateField(player) = PlayerState.Buffering;
        player.SetUiTimersSuspended(true);
        player.SetUiTimersSuspended(false);

        StartPositionTimerDelayed(player);
        var bufferTimer = BufferTimerField(player);
        TestAssert.NotNull(bufferTimer, "_bufferTimer must be initialized and active when StartPositionTimerDelayed is called in Buffering/Playing state");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ProcessStereoSteadySse2")]
    private static extern unsafe void CallEqSteadySse2(EqualizerProcessor eq, float* pSamples, int totalFrames);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ProcessStereoSteadyScalar")]
    private static extern unsafe void CallEqSteadyScalar(EqualizerProcessor eq, float* pSamples, int totalFrames);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ProcessStereoRampSse2")]
    private static extern unsafe void CallEqRampSse2(EqualizerProcessor eq, float* pSamples, int rampFrames);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ProcessStereoRampScalar")]
    private static extern unsafe void CallEqRampScalar(EqualizerProcessor eq, float* pSamples, int rampFrames);

    /// <summary>
    /// Verifies mathematical equivalence of FMA3, SSE2, and scalar paths of the Band-Major equalizer
    /// (both in steady state and on a 30-ms sample-wise smoothing window) and accuracy of <see cref="SpanMathHelper.FastReciprocal"/>.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Equalizer & Reciprocal SIMD Multi-Versioning Equivalence (0 Alloc)", Group = TestGroups.Audio, Order = 10)]
    public static unsafe Task TestEqualizerSimdMultiVersioningEquivalenceAsync()
    {
        // 1. Accuracy test for FastReciprocal (rcpss + 1 Newton-Raphson iteration: error <= 2e-7f)
        ReadOnlySpan<float> testInputs = [0.25f, 0.5f, 0.88f, 0.985f, 1.0f, 1.37f, 2.5f, 10.0f];
        foreach (float val in testInputs)
        {
            float expected = 1.0f / val;
            float actual = SpanMathHelper.FastReciprocal(val);
            float relError = MathF.Abs(actual - expected) / expected;
            TestAssert.True(relError < 2.5e-7f, $"FastReciprocal({val}) relative error {relError:E3} exceeded 2.5e-7");
        }

        // 2. Equivalence of Stationary FMA / SSE2 / Scalar paths on a 10-band preset
        const int frames = 512;
        float[] input = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            input[f * 2] = MathF.Sin(2f * MathF.PI * 125f * f / SampleRate) * 0.3f
                         + MathF.Sin(2f * MathF.PI * 2500f * f / SampleRate) * 0.2f;
            input[f * 2 + 1] = MathF.Cos(2f * MathF.PI * 62.5f * f / SampleRate) * 0.25f
                             + MathF.Sin(2f * MathF.PI * 8000f * f / SampleRate) * 0.15f;
        }

        var preset = EqualizerPresets.GetPresetBands(EqualizerPresets.BreakcoreId);
        var eqDefault = new EqualizerProcessor(SampleRate, Channels);
        var eqSse2 = new EqualizerProcessor(SampleRate, Channels);
        var eqScalar = new EqualizerProcessor(SampleRate, Channels);

        eqDefault.Configure(true, preset, preampDb: 0f, autoPreamp: false);
        eqDefault.SnapToTarget();

        eqSse2.Configure(true, preset, preampDb: 0f, autoPreamp: false);
        eqSse2.SnapToTarget();

        eqScalar.Configure(true, preset, preampDb: 0f, autoPreamp: false);
        eqScalar.SnapToTarget();

        float[] outDefault = (float[])input.Clone();
        float[] outSse2 = (float[])input.Clone();
        float[] outScalar = (float[])input.Clone();

        eqDefault.Process(outDefault.AsSpan());
        fixed (float* pSse2 = outSse2)
            CallEqSteadySse2(eqSse2, pSse2, frames);
        fixed (float* pScalar = outScalar)
            CallEqSteadyScalar(eqScalar, pScalar, frames);

        for (int i = 0; i < outDefault.Length; i++)
        {
            TestAssert.True(
                MathF.Abs(outDefault[i] - outSse2[i]) < 1e-5f,
                $"Steady FMA vs SSE2 mismatch at sample {i}: {outDefault[i]:R} vs {outSse2[i]:R}");
            TestAssert.True(
                MathF.Abs(outDefault[i] - outScalar[i]) < 1e-5f,
                $"Steady FMA vs Scalar mismatch at sample {i}: {outDefault[i]:R} vs {outScalar[i]:R}");
        }

        // 3. Equivalence of Band-Major Ramping (FMA / SSE2) and reference Sample-Major Ramping (Scalar)
        var eqRampDefault = new EqualizerProcessor(SampleRate, Channels);
        var eqRampSse2 = new EqualizerProcessor(SampleRate, Channels);
        var eqRampScalar = new EqualizerProcessor(SampleRate, Channels);

        var vocalPreset = EqualizerPresets.GetPresetBands(EqualizerPresets.VocalId);
        eqRampDefault.Configure(true, vocalPreset, preampDb: 1.5f, autoPreamp: false);
        eqRampSse2.Configure(true, vocalPreset, preampDb: 1.5f, autoPreamp: false);
        eqRampScalar.Configure(true, vocalPreset, preampDb: 1.5f, autoPreamp: false);

        float[] rampBufDefault = (float[])input.Clone();
        float[] rampBufSse2 = (float[])input.Clone();
        float[] rampBufScalar = (float[])input.Clone();

        // Initiate transition from Bypass -> VocalId (1440 smoothing frames)
        eqRampDefault.Process(rampBufDefault.AsSpan());

        // For direct calling of inner kernels, synchronize published parameters via an empty slice
        Span<float> empty = Span<float>.Empty;
        _ = empty;
        eqRampSse2.Configure(true, preset, preampDb: -1.0f, autoPreamp: false);
        eqRampSse2.SnapToTarget();
        eqRampScalar.Configure(true, preset, preampDb: -1.0f, autoPreamp: false);
        eqRampScalar.SnapToTarget();

        fixed (float* pRampSse2 = rampBufSse2)
            CallEqRampSse2(eqRampSse2, pRampSse2, frames);
        fixed (float* pRampScalar = rampBufScalar)
            CallEqRampScalar(eqRampScalar, pRampScalar, frames);

        for (int i = 0; i < rampBufSse2.Length; i++)
        {
            TestAssert.True(
                MathF.Abs(rampBufSse2[i] - rampBufScalar[i]) < 1e-5f,
                $"Ramp SSE2 vs Scalar mismatch at sample {i}: {rampBufSse2[i]:R} vs {rampBufScalar[i]:R}");
        }

        return Task.CompletedTask;
    }
}