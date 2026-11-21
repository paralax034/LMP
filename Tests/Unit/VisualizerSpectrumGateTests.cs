using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Spectrum gate in the audio callback: analysis runs only with an active reader lease and self-resumes via UI polling,
/// resets from another thread are deferred to the audio thread, and levels are published when the block actually plays.
/// </summary>
public static class VisualizerSpectrumGateTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_analyzer3Band")]
    private static extern ref AudioSpectrumAnalyzer SpectrumAnalyzer(AudioPipeline pipeline);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_beatFollowerFast")]
    private static extern ref float BeatFollowerFast(AudioSpectrumAnalyzer analyzer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_beatFollowerSlow")]
    private static extern ref float BeatFollowerSlow(AudioSpectrumAnalyzer analyzer);

    /// <summary>Without readers, the callback outputs PCM without calculating the spectrum; the very first UI poll resumes analysis.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Analysis Suspends Without Readers & Resumes On Poll", Group = TestGroups.Audio, Order = 13)]
    public static async Task TestSpectrumGateFollowsReaderLeaseAsync()
    {
        // Reader lease is global: waiting for its expiration; active UI polling levels makes verification impossible
        await Task.Delay(AudioVisualizerBuffer.ReaderLeaseMs + 100).ConfigureAwait(false);
        if (AudioVisualizerBuffer.HasActiveReaders)
            TestAssert.Skip("AudioVisualizerBuffer is polled by a live UI reader; the reader lease cannot expire");

        var (pipeline, backend) = CreatePipeline(CreateTone(4_096, 1_000f, 0.8f));
        try
        {
            float[] block = new float[2_048];

            int frames = AudioPipelineTestHarness.AudioCallback(pipeline, block);
            TestAssert.Equal(1_024, frames, "Callback must deliver PCM regardless of the spectrum gate");
            TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Spectrum must not be computed while nobody reads it");

            // The read above extended the lease: the next block enters the analyzer again
            AudioPipelineTestHarness.AudioCallback(pipeline, block);
            TestAssert.True(AudioVisualizerBuffer.HasRecentActivity, "A UI poll must resume spectrum analysis on the next callback");

            float level = AudioVisualizerBuffer.OverallLevel;
            TestAssert.True(level > 0.1f, $"Resumed spectrum must reflect the tone, got {level:F3}");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    /// <summary>Stop from another thread does not touch analyzer filters: the reset is performed by the next audio callback.</summary>
    [TestMethod(TestCategory.Unit, "Pipeline Stop Defers Spectrum Reset To The Audio Thread", Group = TestGroups.Audio, Order = 14)]
    public static async Task TestStopDefersSpectrumResetAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);

        var (pipeline, backend) = CreatePipeline(CreateTone(8_192, 60f, 0.8f));
        try
        {
            AudioSpectrumAnalyzer analyzer = SpectrumAnalyzer(pipeline);
            float[] block = new float[2_048];

            // Reading levels extends the lease: analysis is enabled
            _ = AudioVisualizerBuffer.OverallLevel;
            AudioPipelineTestHarness.AudioCallback(pipeline, block);

            float fast = BeatFollowerFast(analyzer);
            float slow = BeatFollowerSlow(analyzer);
            TestAssert.True(fast > 0f && slow > 0f, $"60 Hz tone must charge beat followers: fast={fast}, slow={slow}");

            // Stop from the test thread: published levels clear immediately, while filter state belongs to the audio thread
            pipeline.Stop();
            TestAssert.Equal(fast, BeatFollowerFast(analyzer), "Stop must not mutate analyzer state outside the audio callback");
            TestAssert.Equal(slow, BeatFollowerSlow(analyzer), "Stop must not mutate analyzer state outside the audio callback");
            TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Stop must clear published levels immediately");

            // The next callback performs the deferred reset and analyzes its block from a clean state
            AudioPipelineTestHarness.AudioCallback(pipeline, block);

            var fresh = new AudioSpectrumAnalyzer(SampleRate, Channels);
            fresh.ProcessBlock(block, Channels);
            TestAssert.Equal(BeatFollowerFast(fresh), BeatFollowerFast(analyzer), "Deferred reset must restart the fast follower from zero");
            TestAssert.Equal(BeatFollowerSlow(fresh), BeatFollowerSlow(analyzer), "Deferred reset must restart the slow follower from zero");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    /// <summary>Block levels appear when the block plays, not when it is submitted to the device buffer.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Levels Are Published At Presentation Time", Group = TestGroups.Audio, Order = 15)]
    public static async Task TestSpectrumPresentationDelayAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        float[] loud = CreateTone(2_048, 1_000f, 0.8f);
        float[] silence = new float[2_048 * Channels];
        long delayTicks = Stopwatch.Frequency / 5; // 200 ms in the device buffer

        // 1. The loud block is still queued in the device buffer: too early to display it
        analyzer.ProcessBlock(loud, Channels, delayTicks);
        TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Levels must not lead the audible output");

        // 2. Playback time has passed: the next block publishes the loud frame and queues its silent one
        await Task.Delay(260).ConfigureAwait(false);
        analyzer.ProcessBlock(silence, Channels, delayTicks);

        float level = AudioVisualizerBuffer.OverallLevel;
        TestAssert.True(level > 0.5f, $"Due loud frame must be published once its presentation time passes, got {level:F3}");

        // 3. Without delay, the current block is published immediately, and the queue is flushed
        analyzer.ProcessBlock(silence, Channels);
        TestAssert.True(AudioVisualizerBuffer.OverallLevel < 0.01f, "Zero delay must publish the current block immediately");

        // 4. Presentation delay uses device sample rate (not decoder rate) and yields 0 on degenerate inputs
        long tenthSecond = Stopwatch.Frequency / 10;
        long delay = AudioPipeline.ComputePresentationDelayTicks(4_800 * 2, 48_000, 2);
        TestAssert.Equal(tenthSecond, delay, "4800 device frames at 48 kHz must delay levels by exactly 100 ms");
        TestAssert.Equal(0L, AudioPipeline.ComputePresentationDelayTicks(0, 48_000, 2));
        TestAssert.Equal(0L, AudioPipeline.ComputePresentationDelayTicks(9_600, 0, 2));
        TestAssert.Equal(0L, AudioPipeline.ComputePresentationDelayTicks(9_600, 48_000, 0));
    }

    /// <summary>
    /// Verifies perceptual audio feature accuracy and bug regressions:
    /// exact L/R HF stereo symmetry (no −15 dB right-channel attenuation), 250 Hz coverage,
    /// center vocal vs anti-phase stereo discrimination, kick SuperFlux onset vs steady bass drone,
    /// spectral centroid monotonicity, digital silence sleep gate, and 0 B snapshot reads.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Spectrum Stereo Symmetry, 250Hz, Vocal/Kick/Centroid & Silence Sleep (0 Alloc)", Group = TestGroups.Audio, Order = 16)]
    public static async Task TestSpectrumMusicalFeaturesAndSymmetryAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        const int blockFrames = 1_024;
        float[] block = new float[blockFrames * Channels];
        Span<float> bandsL = stackalloc float[10];
        Span<float> bandsR = stackalloc float[10];

        // 1. Exact L/R HF stereo symmetry at 8 kHz (regression for former 0.85*L + 0.15*R −15 dB right-channel bias)
        analyzer.Reset();
        for (int f = 0; f < blockFrames; f++)
        {
            float s = 0.5f * MathF.Sin(2f * MathF.PI * 8_000f * f / SampleRate);
            block[f * 2] = s;
            block[f * 2 + 1] = 0f;
        }
        for (int i = 0; i < 4; i++) analyzer.ProcessBlock(block, Channels);
        AudioVisualizerBuffer.CopyBands10(bandsL);
        bool okL = AudioVisualizerBuffer.TryReadSnapshot(out var snapL);

        analyzer.Reset();
        for (int f = 0; f < blockFrames; f++)
        {
            float s = 0.5f * MathF.Sin(2f * MathF.PI * 8_000f * f / SampleRate);
            block[f * 2] = 0f;
            block[f * 2 + 1] = s;
        }
        for (int i = 0; i < 4; i++) analyzer.ProcessBlock(block, Channels);
        AudioVisualizerBuffer.CopyBands10(bandsR);
        bool okR = AudioVisualizerBuffer.TryReadSnapshot(out var snapR);

        TestAssert.True(okL && okR, "Hard-panned L and R 8 kHz tones must produce active snapshots");
        TestAssert.True(
            MathF.Abs(snapL.High - snapR.High) < 0.01f && MathF.Abs(bandsL[8] - bandsR[8]) < 0.01f,
            $"8 kHz L/R asymmetry detected: High L={snapL.High:F4} vs R={snapR.High:F4}, Band8 L={bandsL[8]:F4} vs R={bandsR[8]:F4}");

        // 2. 250 Hz (band 3) must contribute to MidLevel and LowLevel (regression for dropped band[3])
        analyzer.Reset();
        float[] tone250 = CreateTone(blockFrames, 250f, 0.5f);
        for (int i = 0; i < 4; i++) analyzer.ProcessBlock(tone250, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snap250));
        TestAssert.True(snap250.Mid > 0.35f, $"250 Hz must register strongly in MidLevel, got {snap250.Mid:F3}");
        TestAssert.True(snap250.Low > 0.20f, $"250 Hz must register in LowLevel warmth, got {snap250.Low:F3}");

        // 3. Center vocal (1 kHz L==R) vs wide anti-phase stereo (1 kHz L==-R)
        analyzer.Reset();
        float[] vocalCenter = CreateTone(blockFrames, 1_000f, 0.45f);
        for (int i = 0; i < 4; i++) analyzer.ProcessBlock(vocalCenter, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapVocal));

        analyzer.Reset();
        for (int f = 0; f < blockFrames; f++)
        {
            float s = 0.45f * MathF.Sin(2f * MathF.PI * 1_000f * f / SampleRate);
            block[f * 2] = s;
            block[f * 2 + 1] = -s;
        }
        for (int i = 0; i < 4; i++) analyzer.ProcessBlock(block, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapAntiPhase));

        TestAssert.True(snapVocal.Vocal > 0.50f, $"Center 1 kHz vocal must produce high VocalPresence, got {snapVocal.Vocal:F3}");
        TestAssert.True(snapAntiPhase.Vocal < 0.05f, $"Anti-phase wide stereo must not trigger VocalPresence, got {snapAntiPhase.Vocal:F3}");
        TestAssert.True(snapAntiPhase.Mid > 0.40f, $"Anti-phase stereo must preserve total Mid energy via Side filterbank, got {snapAntiPhase.Mid:F3}");

        // 4. Spectral centroid monotonicity: 62.5 Hz < 1 kHz < 8 kHz
        analyzer.Reset();
        float[] toneLow = CreateTone(blockFrames, 62.5f, 0.5f);
        for (int i = 0; i < 8; i++) analyzer.ProcessBlock(toneLow, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapLow));
        float centroidLow = snapLow.SpectralCentroid;

        analyzer.Reset();
        float[] toneMid = CreateTone(blockFrames, 1_000f, 0.5f);
        for (int i = 0; i < 8; i++) analyzer.ProcessBlock(toneMid, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapMid));
        float centroidMid = snapMid.SpectralCentroid;

        analyzer.Reset();
        float[] toneHigh = CreateTone(blockFrames, 8_000f, 0.5f);
        for (int i = 0; i < 8; i++) analyzer.ProcessBlock(toneHigh, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapHigh));
        float centroidHigh = snapHigh.SpectralCentroid;

        TestAssert.True(
            centroidLow < 0.25f && centroidMid > 0.42f && centroidMid < 0.68f && centroidHigh > 0.78f,
            $"Spectral centroid must track timbre monotonically: low={centroidLow:F3}, mid={centroidMid:F3}, high={centroidHigh:F3}");

        // 5. Kick transient onset vs steady bass drone (SuperFlux + dt-independent beat follower)
        analyzer.Reset();
        float[] bassDrone = CreateTone(blockFrames, 62.5f, 0.16f);
        for (int i = 0; i < 25; i++) analyzer.ProcessBlock(bassDrone, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapDrone));

        float[] kickHit = CreateTone(blockFrames, 62.5f, 0.80f);
        analyzer.ProcessBlock(kickHit, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapKick));

        TestAssert.True(snapDrone.Beat < 0.15f && snapDrone.BassFlux < 0.08f,
            $"Sustained bass drone must settle without false beat spikes: beat={snapDrone.Beat:F3}, flux={snapDrone.BassFlux:F3}");
        TestAssert.True(snapKick.Beat > 0.35f && snapKick.BassFlux > 0.20f,
            $"Kick transient must spike Beat and BassFlux: beat={snapKick.Beat:F3}, flux={snapKick.BassFlux:F3}");

        // 6. Zero-alloc hot-path verification for ProcessBlock + TryReadSnapshot
        TestAssert.ZeroAlloc(() =>
        {
            analyzer.ProcessBlock(vocalCenter, Channels);
            _ = AudioVisualizerBuffer.TryReadSnapshot(out _);
        }, warmup: 50, iterations: 500, context: "AudioSpectrumAnalyzer + TryReadSnapshot");

        // 7. Sustained digital silence must let HasRecentActivity expire so UI/render threads sleep
        float[] silence = new float[blockFrames * Channels];
        analyzer.ProcessBlock(silence, Channels);
        await Task.Delay(65).ConfigureAwait(false);
        analyzer.ProcessBlock(silence, Channels);
        TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Sustained digital silence must expire HasRecentActivity");
        TestAssert.Equal(0f, AudioVisualizerBuffer.OverallLevel);

        analyzer.Reset();
    }

    /// <summary>
    /// Verifies unlocked macro phrase dynamics (quiet verse -> sudden drop sustains >= 2.5x contrast without fast AGC collapse)
    /// and causal HPSS separation (sustained center vocal vs sharp broadband percussive snare crack).
    /// </summary>
    [TestMethod(TestCategory.Unit, "Spectrum Macro Verse-to-Drop Contrast & Causal HPSS (Vocal vs Snare)", Group = TestGroups.Audio, Order = 17)]
    public static async Task TestMacroVerseToDropAndCausalHpssSeparationAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        const int blockFrames = 960; // 20 ms @ 48 kHz

        // 1. Quiet verse (~2.4 seconds @ amp=0.07) -> sudden drop (~0.6 seconds @ amp=0.55)
        float[] quietVerse = CreateTone(blockFrames, 125f, 0.07f);
        for (int i = 0; i < 120; i++)
            analyzer.ProcessBlock(quietVerse, Channels);

        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapVerse));

        float[] dropBlock = new float[blockFrames * Channels];
        for (int f = 0; f < blockFrames; f++)
        {
            float s = 0.45f * MathF.Sin(2f * MathF.PI * 62.5f * f / SampleRate)
                    + 0.25f * MathF.Sin(2f * MathF.PI * 1_000f * f / SampleRate)
                    + 0.15f * MathF.Sin(2f * MathF.PI * 4_000f * f / SampleRate);
            dropBlock[f * 2] = s;
            dropBlock[f * 2 + 1] = s;
        }

        analyzer.ProcessBlock(dropBlock, Channels);
        analyzer.ProcessBlock(dropBlock, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapDropStart));

        // Continue 600 ms into the drop: slow track reference must NOT collapse MacroEnergy back to verse level
        for (int i = 0; i < 30; i++)
            analyzer.ProcessBlock(dropBlock, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapDropSustained));

        TestAssert.True(
            snapDropStart.MacroEnergy > snapVerse.MacroEnergy * 2.5f && snapDropStart.DropImpact > 0.25f,
            $"Drop onset must explode relative to quiet verse: verse={snapVerse.MacroEnergy:F3}, drop={snapDropStart.MacroEnergy:F3}, impact={snapDropStart.DropImpact:F3}");
        TestAssert.True(
            snapDropSustained.MacroEnergy > snapVerse.MacroEnergy * 2.0f,
            $"Sustained drop (600ms) must preserve macro contrast without fast AGC collapse: verse={snapVerse.MacroEnergy:F3}, drop600ms={snapDropSustained.MacroEnergy:F3}");

        // 2. Sustained center vocal (1 kHz) -> high HarmonicEnergy & Vocal, low PercussiveEnergy
        analyzer.Reset();
        float[] sustainedVocal = CreateTone(blockFrames, 1_000f, 0.45f);
        for (int i = 0; i < 16; i++)
            analyzer.ProcessBlock(sustainedVocal, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapVocal));

        TestAssert.True(
            snapVocal.Vocal > 0.50f && snapVocal.HarmonicEnergy > snapVocal.PercussiveEnergy * 2.0f,
            $"Sustained center vocal must classify as Harmonic Vocal: vocal={snapVocal.Vocal:F3}, harm={snapVocal.HarmonicEnergy:F3}, perc={snapVocal.PercussiveEnergy:F3}");

        // 3. Sharp broadband snare crack (20 ms impulse after silence) -> high PercussiveEnergy & SnareFlux, suppressed Vocal
        analyzer.Reset();
        float[] silence = new float[blockFrames * Channels];
        for (int i = 0; i < 5; i++)
            analyzer.ProcessBlock(silence, Channels);

        float[] snareCrack = new float[blockFrames * Channels];
        for (int f = 0; f < blockFrames; f++)
        {
            float env = MathF.Exp(-f / (SampleRate * 0.008f));
            float s = env * (0.45f * MathF.Sin(2f * MathF.PI * 250f * f / SampleRate)
                           + 0.40f * MathF.Sin(2f * MathF.PI * 2_000f * f / SampleRate)
                           + 0.35f * MathF.Sin(2f * MathF.PI * 4_000f * f / SampleRate));
            snareCrack[f * 2] = s;
            snareCrack[f * 2 + 1] = s;
        }

        analyzer.ProcessBlock(snareCrack, Channels);
        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapSnare));

        TestAssert.True(
            snapSnare.PercussiveEnergy > 0.35f && snapSnare.SnareFlux > 0.25f,
            $"Snare hit must spike PercussiveEnergy & SnareFlux: perc={snapSnare.PercussiveEnergy:F3}, snare={snapSnare.SnareFlux:F3}");
        TestAssert.True(
            snapSnare.Vocal < 0.25f,
            $"Brief percussive snare hit must not falsely trigger sustained Vocal presence: vocal={snapSnare.Vocal:F3}");

        analyzer.Reset();
    }

    /// <summary>
    /// Verifies left-to-right hydrodynamic flow, heavy-bass headroom without flat-top clipping, zero-pop exit continuity,
    /// dispersion, 0 B allocations in <see cref="LMP.UI.Controls.AudioWaveBorder"/>, and both SkSL shaders,
    /// non-evicting full-width travel, reactivity calibration, and smooth pause fade in <see cref="LMP.UI.Controls.AmbientBackdrop"/>.
    /// </summary>
    [TestMethod(TestCategory.Unit, "WaveBorder Left-to-Right Flow, Heavy-Bass Headroom, Zero-Pop Exit & SkSL Shaders (0 Alloc)", Group = TestGroups.Audio, Order = 18)]
    public static Task TestWaveBorderLeftToRightFlowAndBackdropShadersAsync()
    {
        var packets = new LMP.UI.Controls.AudioWaveBorder.WavePacket[LMP.UI.Controls.AudioWaveBorder.MaxWavePackets];
        float[] disp = new float[LMP.UI.Controls.AudioWaveBorder.RailSamples];
        float[] prevDisp = new float[LMP.UI.Controls.AudioWaveBorder.RailSamples];

        // 1. Immediate visibility near left edge within 0..80 ms audiovisual binding window (no off-screen blind zone)
        LMP.UI.Controls.AudioWaveBorder.SpawnPacket(
            packets,
            LMP.UI.Controls.AudioWaveBorder.WavePacketKind.KickBullet,
            amplitude: 2.4f,
            centroid: 0.35f,
            speedMult: 1.0);

        LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
        LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0); // ~33 ms
        LMP.UI.Controls.AudioWaveBorder.EvaluateRailDisplacement(packets, disp, vocalAmp: 0f, harmAmp: 0f, vocalPhase: 0f, centroid: 0.35f);

        float leftEdgePeak = 0f;
        for (int i = 1; i <= 14; i++)
            leftEdgePeak = MathF.Max(leftEdgePeak, MathF.Abs(disp[i]));
        TestAssert.True(leftEdgePeak > 0.15f, $"Wave must be visible at left edge within 33 ms, got {leftEdgePeak:F3}");

        // 2. Strict left-to-right propagation + bounded non-clipping finite values + zero-pop deactivation
        static float ComputeCenterOfMass(ReadOnlySpan<float> d)
        {
            float num = 0f;
            float den = 0f;
            for (int i = 1; i < d.Length - 1; i++)
            {
                float a = MathF.Abs(d[i]);
                num += i * a;
                den += a;
            }
            return den > 1e-5f ? num / den : -1f;
        }

        float com1 = ComputeCenterOfMass(disp);
        float globalMax = 0f;
        float com2 = 0f;
        float com3 = 0f;
        float deactivationJump = 0f;
        bool wasActive = true;

        Array.Copy(disp, prevDisp, disp.Length);
        for (int f = 1; f <= 260; f++)
        {
            bool stillActive = LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
            LMP.UI.Controls.AudioWaveBorder.EvaluateRailDisplacement(packets, disp, vocalAmp: 0f, harmAmp: 0f, vocalPhase: 0f, centroid: 0.35f);

            float frameJump = 0f;
            for (int i = 0; i < disp.Length; i++)
            {
                TestAssert.True(float.IsFinite(disp[i]), $"Non-finite wave displacement at frame {f}, idx {i}");
                globalMax = MathF.Max(globalMax, MathF.Abs(disp[i]));
                frameJump = MathF.Max(frameJump, MathF.Abs(disp[i] - prevDisp[i]));
            }

            if (f == 24) com2 = ComputeCenterOfMass(disp);
            if (f == 48) com3 = ComputeCenterOfMass(disp);
            if (wasActive && !stillActive)
                deactivationJump = frameJump;

            wasActive = stillActive;
            Array.Copy(disp, prevDisp, disp.Length);
            if (!stillActive) break;
        }

        TestAssert.True(
            com1 > 0f && com2 > com1 + 4f && com3 > com2 + 4f,
            $"Wave center-of-mass must strictly travel left-to-right: t1={com1:F2}, t2={com2:F2}, t3={com3:F2}");
        TestAssert.True(
            globalMax < LMP.UI.Controls.AudioWaveBorder.MaxSafeDisplacement,
            $"Displacement must stay smoothly below tanh ceiling {LMP.UI.Controls.AudioWaveBorder.MaxSafeDisplacement:F2}, got {globalMax:F3}");
        TestAssert.True(
            !wasActive && deactivationJump < 0.02f,
            $"Packet must fade completely to zero before deactivation (no abrupt snap), jump={deactivationJump:F4}");

        // 3. Heavy-bass stress (12 dense Kick/Bass packets): never peg flat at max, real crests & valleys preserved, never evicts mid-rail packets
        Array.Clear(packets);
        LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, LMP.UI.Controls.AudioWaveBorder.WavePacketKind.KickBullet, 2.4f, 0.3f, 1.0);
        for (int f = 0; f < 45; f++) // move packet[0] to mid-rail (~0.42)
            LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
        float trackedMidPos = packets[0].Position;

        for (int k = 0; k < LMP.UI.Controls.AudioWaveBorder.MaxWavePackets + 8; k++)
        {
            var kind = (k & 1) == 0
                ? LMP.UI.Controls.AudioWaveBorder.WavePacketKind.KickBullet
                : LMP.UI.Controls.AudioWaveBorder.WavePacketKind.BassSwell;
            LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, kind, 2.5f, 0.25f, 1.0);
            LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
        }

        TestAssert.True(
            packets[0].IsActive && packets[0].Position > trackedMidPos,
            $"Saturating the packet pool must NEVER evict an active mid-rail wave: active={packets[0].IsActive}, pos={packets[0].Position:F3}");

        LMP.UI.Controls.AudioWaveBorder.EvaluateRailDisplacement(packets, disp, vocalAmp: 0.2f, harmAmp: 0.2f, vocalPhase: 0.8f, centroid: 0.3f);
        float heavyMax = float.MinValue;
        float heavyMin = float.MaxValue;
        for (int i = 6; i < disp.Length - 6; i++)
        {
            heavyMax = MathF.Max(heavyMax, disp[i]);
            heavyMin = MathF.Min(heavyMin, disp[i]);
        }

        TestAssert.True(
            heavyMax < LMP.UI.Controls.AudioWaveBorder.MaxSafeDisplacement && (heavyMax - heavyMin) > 0.80f && heavyMin < 0.40f,
            $"Heavy-bass rail must oscillate dynamically without pegging flat at ceiling: min={heavyMin:F3}, max={heavyMax:F3}");

        // 4. Physical dispersion: HighNeedle overtakes BassSwell spawned simultaneously
        Array.Clear(packets);
        LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, LMP.UI.Controls.AudioWaveBorder.WavePacketKind.BassSwell, 1.8f, 0.3f, 1.0);
        LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, LMP.UI.Controls.AudioWaveBorder.WavePacketKind.HighNeedle, 1.5f, 0.8f, 1.0);
        for (int f = 0; f < 30; f++)
            LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
        TestAssert.True(
            packets[1].Position > packets[0].Position + 0.15f,
            $"HighNeedle must overtake BassSwell via dispersion: high={packets[1].Position:F3}, bass={packets[0].Position:F3}");

        // 5. Zero-alloc hot-path verification
        TestAssert.ZeroAlloc(() =>
        {
            LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
            LMP.UI.Controls.AudioWaveBorder.EvaluateRailDisplacement(packets, disp, vocalAmp: 0.4f, harmAmp: 0.3f, vocalPhase: 1.2f, centroid: 0.55f);
        }, warmup: 20, iterations: 500, context: "AudioWaveBorder.StepPackets + EvaluateRailDisplacement");

        // 6. AmbientBackdrop FluidWave: full-width travel (never dies mid-screen), zero mid-screen eviction, smooth exit to 0
        var fluidSlots = new LMP.UI.Controls.AmbientBackdrop.WaveSlot[LMP.UI.Controls.AmbientBackdrop.MaxFluidWavePackets];
        LMP.UI.Controls.AmbientBackdrop.SpawnFluidPacketCore(fluidSlots, halfWidth: 0.21f, speed: 0.55f, amplitude: 0.85f, tint: -0.7f);
        for (int f = 0; f < 60; f++) // ~1.0 s -> should be around Position ~0.56 with strong amplitude
            LMP.UI.Controls.AmbientBackdrop.StepFluidPacketsCore(fluidSlots, 1f / 60f, isPlaying: true);

        float midScreenPos = fluidSlots[0].Position;
        float midScreenAmp = LMP.UI.Controls.AmbientBackdrop.ComputeFluidEffectiveAmplitude(in fluidSlots[0]);
        TestAssert.True(
            fluidSlots[0].IsActive && midScreenPos > 0.45f && midScreenAmp > 0.50f,
            $"FluidWave packet must stay alive & strong across mid-screen: active={fluidSlots[0].IsActive}, pos={midScreenPos:F3}, amp={midScreenAmp:F3}");

        // Saturate all 12 slots + overflow: mid-screen wave must NEVER be evicted
        for (int s = 0; s < LMP.UI.Controls.AmbientBackdrop.MaxFluidWavePackets + 8; s++)
            LMP.UI.Controls.AmbientBackdrop.SpawnFluidPacketCore(fluidSlots, halfWidth: 0.16f, speed: 0.65f, amplitude: 0.75f, tint: 0.3f);
        TestAssert.True(
            fluidSlots[0].IsActive && MathF.Abs(fluidSlots[0].Position - midScreenPos) < 0.001f,
            "FluidWave pool saturation must NEVER overwrite a visible mid-screen wave");

        // Advance until packet[0] exits right edge and verify smooth fade to exact 0 before deactivation
        float lastAmpBeforeExit = midScreenAmp;
        for (int f = 0; f < 180 && fluidSlots[0].IsActive; f++)
        {
            lastAmpBeforeExit = LMP.UI.Controls.AmbientBackdrop.ComputeFluidEffectiveAmplitude(in fluidSlots[0]);
            LMP.UI.Controls.AmbientBackdrop.StepFluidPacketsCore(fluidSlots, 1f / 60f, isPlaying: true);
        }
        TestAssert.True(
            !fluidSlots[0].IsActive && lastAmpBeforeExit < 0.02f,
            $"FluidWave packet must glide off right edge and dissolve smoothly to 0 before deactivation: active={fluidSlots[0].IsActive}, lastAmp={lastAmpBeforeExit:F4}");

        // 7. Both AmbientBackdrop GPU SkSL shaders must compile cleanly
        TestAssert.True(
            LMP.UI.Controls.AmbientBackdrop.IsFluidWaveShaderCompiled,
            "AmbientBackdrop FluidWave GPU SkSL shader failed to compile");
        TestAssert.True(
            LMP.UI.Controls.AmbientBackdrop.IsAuroraShaderCompiled,
            "AmbientBackdrop Aurora GPU SkSL shader failed to compile");

        // 8. Backdrop reactivity calibration (0% steady, 60% balanced raised floor / tamed peak, 100% wide range) & smooth pause fade
        double steadyQuiet = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
            playingBlend: 1.0, macroEnergy: 0.12, kickPos: 0.0, vocalAura: 0.05, dropBloom: 0.0, reactivity: 0.0);
        double steadyDrop = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
            playingBlend: 1.0, macroEnergy: 1.15, kickPos: 0.90, vocalAura: 0.40, dropBloom: 0.80, reactivity: 0.0);
        TestAssert.True(
            Math.Abs(steadyDrop - steadyQuiet) < 0.001,
            $"0% reactivity must keep steady brightness: quiet={steadyQuiet:F3}, drop={steadyDrop:F3}");

        double quietEnergy = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
            playingBlend: 1.0, macroEnergy: 0.12, kickPos: 0.0, vocalAura: 0.05, dropBloom: 0.0, reactivity: 0.60);
        double dropEnergy = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
            playingBlend: 1.0, macroEnergy: 1.15, kickPos: 0.90, vocalAura: 0.40, dropBloom: 0.80, reactivity: 0.60);
        TestAssert.True(
            quietEnergy >= 0.52 && dropEnergy <= 0.88 && dropEnergy > quietEnergy * 1.35,
            $"Default 60% reactivity must maintain visible floor and tamed peak: quiet={quietEnergy:F3}, drop={dropEnergy:F3}");

        double fullQuiet = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
            playingBlend: 1.0, macroEnergy: 0.12, kickPos: 0.0, vocalAura: 0.05, dropBloom: 0.0, reactivity: 1.0);
        double fullDrop = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
            playingBlend: 1.0, macroEnergy: 1.15, kickPos: 0.90, vocalAura: 0.40, dropBloom: 0.80, reactivity: 1.0);
        TestAssert.True(
            fullDrop > fullQuiet * 1.75,
            $"100% reactivity must provide strong verse-to-drop contrast: quiet={fullQuiet:F3}, drop={fullDrop:F3}");

        double prevE = quietEnergy;
        for (int step = 1; step <= 20; step++)
        {
            double blend = 1.0 - (step / 20.0);
            double curE = LMP.UI.Controls.AmbientBackdrop.ComputeDynamicEnergy(
                playingBlend: blend, macroEnergy: 0.12 * blend, kickPos: 0.0, vocalAura: 0.05 * blend, dropBloom: 0.0, reactivity: 0.60);
            double delta = Math.Abs(curE - prevE);
            TestAssert.True(delta < 0.04, $"Discontinuous brightness pop detected during pause fade: delta={delta:F4}");
            prevE = curE;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that quiet mid-frequency sci-fi/synth beats right after track start (<see cref="AudioSpectrumAnalyzer.Reset"/>)
    /// reliably trigger <see cref="AudioVisualizerSnapshot.MidFlux"/> without getting swallowed by initial AGC/SuperFlux floors,
    /// while preserving dramatic verse-to-drop macro contrast.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Spectrum Quiet Intro Sci-Fi Mid Beats & Preserved Drop Contrast", Group = TestGroups.Audio, Order = 19)]
    public static async Task TestQuietSciFiMidBeatsAtTrackStartAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        const int blockFrames = 960; // 20 ms @ 48 kHz

        float[] silence = new float[blockFrames * Channels];
        float[] synthPulse = new float[blockFrames * Channels];
        for (int f = 0; f < blockFrames; f++)
        {
            float env = MathF.Exp(-f / (SampleRate * 0.010f));
            float s = env * 0.06f * (0.55f * MathF.Sin(2f * MathF.PI * 750f * f / SampleRate)
                                   + 0.45f * MathF.Sin(2f * MathF.PI * 1_150f * f / SampleRate));
            synthPulse[f * 2] = s;
            synthPulse[f * 2 + 1] = s;
        }

        analyzer.Reset();

        float minPulseMidFlux = float.MaxValue;
        float introMacro = 0f;

        // 4 rhythmic quiet sci-fi pulses immediately at track start (every 200 ms)
        for (int beat = 0; beat < 4; beat++)
        {
            for (int g = 0; g < 9; g++)
                analyzer.ProcessBlock(silence, Channels);

            analyzer.ProcessBlock(synthPulse, Channels);
            TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapPulse), $"Pulse #{beat + 1} must be active");
            minPulseMidFlux = MathF.Min(minPulseMidFlux, snapPulse.MidFlux);
            introMacro = snapPulse.MacroEnergy;
        }

        TestAssert.True(
            minPulseMidFlux > 0.15f,
            $"Quiet intro sci-fi mid pulses (amp=0.06) must reliably exceed MidFlux > 0.15, got min={minPulseMidFlux:F3}");

        // Followed by a full-energy drop: macro contrast must still explode >= 2.5x
        float[] dropBlock = new float[blockFrames * Channels];
        for (int f = 0; f < blockFrames; f++)
        {
            float s = 0.45f * MathF.Sin(2f * MathF.PI * 62.5f * f / SampleRate)
                    + 0.25f * MathF.Sin(2f * MathF.PI * 1_000f * f / SampleRate)
                    + 0.15f * MathF.Sin(2f * MathF.PI * 4_000f * f / SampleRate);
            dropBlock[f * 2] = s;
            dropBlock[f * 2 + 1] = s;
        }

        for (int i = 0; i < 4; i++)
            analyzer.ProcessBlock(dropBlock, Channels);

        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapDrop));
        TestAssert.True(
            snapDrop.MacroEnergy > MathF.Max(introMacro, 0.05f) * 2.5f,
            $"Drop after quiet sci-fi intro must preserve strong macro contrast: intro={introMacro:F3}, drop={snapDrop.MacroEnergy:F3}");

        analyzer.Reset();
    }

    /// <summary>
    /// Stress-tests concurrent AudioVisualizerBuffer.Update10Band (WASAPI thread) vs Reset (actor thread)
    /// to verify SeqLock _seqVersion parity never corrupts to odd-when-idle and TryReadSnapshot remains atomic.
    /// </summary>
    [TestMethod(TestCategory.Unit, "VisualizerBuffer Multi-Writer SeqLock Parity & Reset Stress", Group = TestGroups.Audio, Order = 20)]
    public static async Task TestVisualizerBufferConcurrentResetAndUpdateSeqLockAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var token = cts.Token;

        var updateTask = Task.Run(() =>
        {
            float[] bands = [0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.6f, 0.5f, 0.4f, 0.3f];
            while (!token.IsCancellationRequested)
            {
                AudioVisualizerBuffer.Update10Band(
                    bands,
                    low: 0.5f,
                    mid: 0.6f,
                    high: 0.4f,
                    beat: 0.3f,
                    overall: 0.7f,
                    dominantBand: 5);
            }
        });

        var resetTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                AudioVisualizerBuffer.Reset();
            }
        });

        while (!token.IsCancellationRequested)
        {
            _ = AudioVisualizerBuffer.TryReadSnapshot(out _);
        }

        await Task.WhenAll(updateTask, resetTask).ConfigureAwait(false);

        long seqAfterStress = AudioVisualizerBuffer.CurrentSeqVersion;
        TestAssert.Equal(0L, seqAfterStress & 1L, $"SeqLock parity corrupted to odd ({seqAfterStress}) under concurrent Update10Band/Reset");

        AudioVisualizerBuffer.Reset();
        TestAssert.Equal(0L, AudioVisualizerBuffer.CurrentSeqVersion & 1L);
        TestAssert.False(AudioVisualizerBuffer.HasRecentActivity);
    }

    /// <summary>
    /// Verifies that real-world 10 ms WASAPI blocks with a loud broadband bed (125 Hz bass + 1 kHz / 2 kHz mids)
    /// do not mask quieter 8 kHz hi-hat hits or 62.5 Hz kick attacks in adjacent octaves.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Spectrum Broadband 10ms Mix: No Cross-Octave Hi-Hat/Kick Masking", Group = TestGroups.Audio, Order = 21)]
    public static async Task TestBroadband10MsMixNoCrossOctaveMaskingAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        const int blockFrames = 480; // 10 ms @ 48 kHz (matches real WASAPI callback size)
        float[] block = new float[blockFrames * Channels];
        int phaseOffset = 0;

        void FillBed(float hiHatAmp, float kickAmp)
        {
            for (int f = 0; f < blockFrames; f++)
            {
                int n = phaseOffset + f;
                float s = (0.30f * MathF.Sin(2f * MathF.PI * 125f * n / SampleRate))
                        + (0.18f * MathF.Sin(2f * MathF.PI * 1_000f * n / SampleRate))
                        + (0.14f * MathF.Sin(2f * MathF.PI * 2_000f * n / SampleRate));

                if (hiHatAmp > 0f)
                    s += hiHatAmp * MathF.Sin(2f * MathF.PI * 8_000f * n / SampleRate);

                if (kickAmp > 0f)
                    s += kickAmp * MathF.Sin(2f * MathF.PI * 62.5f * n / SampleRate);

                block[f * 2] = s;
                block[f * 2 + 1] = s;
            }
            phaseOffset += blockFrames;
        }

        // 1. Settle steady broadband bed for 400 ms (40 x 10 ms)
        for (int i = 0; i < 40; i++)
        {
            FillBed(hiHatAmp: 0f, kickAmp: 0f);
            analyzer.ProcessBlock(block, Channels);
        }

        TestAssert.True(AudioVisualizerBuffer.TryReadSnapshot(out var snapBed));
        TestAssert.True(
            snapBed.BassFlux < 0.10f && snapBed.HighFlux < 0.10f,
            $"Steady broadband bed must settle cleanly: bassFlux={snapBed.BassFlux:F3}, highFlux={snapBed.HighFlux:F3}");

        // 2. Quieter 8 kHz hi-hat (amp=0.08) on top of louder 125 Hz + 1–2 kHz bed (30 ms rising burst)
        float maxHighFlux = 0f;
        for (int i = 1; i <= 3; i++)
        {
            FillBed(hiHatAmp: 0.08f * (i / 3f), kickAmp: 0f);
            analyzer.ProcessBlock(block, Channels);
            if (AudioVisualizerBuffer.TryReadSnapshot(out var snapHat))
                maxHighFlux = MathF.Max(maxHighFlux, snapHat.HighFlux);
        }

        TestAssert.True(
            maxHighFlux > 0.25f,
            $"8 kHz hi-hat (amp=0.08) must not be masked by louder 125 Hz / 2 kHz bed on 10 ms blocks: maxHighFlux={maxHighFlux:F3}");

        // 3. Re-settle bed for 300 ms, then inject 62.5 Hz kick (amp=0.22) under louder 125 Hz bass (amp=0.30)
        for (int i = 0; i < 30; i++)
        {
            FillBed(hiHatAmp: 0f, kickAmp: 0f);
            analyzer.ProcessBlock(block, Channels);
        }

        float maxBassFlux = 0f;
        for (int i = 1; i <= 3; i++)
        {
            FillBed(hiHatAmp: 0f, kickAmp: 0.22f * (i / 3f));
            analyzer.ProcessBlock(block, Channels);
            if (AudioVisualizerBuffer.TryReadSnapshot(out var snapKick))
                maxBassFlux = MathF.Max(maxBassFlux, snapKick.BassFlux);
        }

        TestAssert.True(
            maxBassFlux > 0.20f,
            $"62.5 Hz kick (amp=0.22) must not be masked by louder 125 Hz bass (amp=0.30) on 10 ms blocks: maxBassFlux={maxBassFlux:F3}");

        analyzer.Reset();
    }

    /// <summary>
    /// Verifies that saturating the wave packet pool during a dense 180 BPM chorus coalesces fresh kicks
    /// at the left entrance instead of dropping them, and preserves strong peak-to-peak rail oscillation.
    /// </summary>
    [TestMethod(TestCategory.Unit, "WaveBorder Dense Chorus Entry Coalescing & Headroom", Group = TestGroups.Audio, Order = 22)]
    public static Task TestWaveBorderDenseChorusCoalescingAndHeadroomAsync()
    {
        var packets = new LMP.UI.Controls.AudioWaveBorder.WavePacket[LMP.UI.Controls.AudioWaveBorder.MaxWavePackets];
        float[] disp = new float[LMP.UI.Controls.AudioWaveBorder.RailSamples];

        // Fill 23 slots with mid-rail waves (Position > 0.25)
        for (int i = 0; i < packets.Length - 1; i++)
        {
            var kind = (LMP.UI.Controls.AudioWaveBorder.WavePacketKind)(i % 4);
            LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, kind, amplitude: 1.5f, centroid: 0.45f, speedMult: 1.0);
            for (int step = 0; step < 3; step++)
                LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);
        }

        // Fill the last slot with a fresh kick and advance by exactly one kick cooldown (160 ms)
        int entrySlot = packets.Length - 1;
        LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, LMP.UI.Controls.AudioWaveBorder.WavePacketKind.KickBullet, amplitude: 1.4f, centroid: 0.35f, speedMult: 1.0);
        for (int f = 0; f < 10; f++) // ~166 ms
            LMP.UI.Controls.AudioWaveBorder.StepPackets(packets, dt: 1.0 / 60.0, speedMult: 1.0);

        float ampBeforeMerge = packets[entrySlot].Amplitude;
        TestAssert.True(packets[entrySlot].IsActive && packets[entrySlot].Position < 0.16f,
            $"Fresh kick after 166 ms must remain inside left-edge merge window (<0.16), got pos={packets[entrySlot].Position:F3}");

        // Next kick arrives while pool is full: must coalesce into entrySlot instead of silently dropping
        LMP.UI.Controls.AudioWaveBorder.SpawnPacket(packets, LMP.UI.Controls.AudioWaveBorder.WavePacketKind.KickBullet, amplitude: 2.0f, centroid: 0.35f, speedMult: 1.0);
        TestAssert.True(
            packets[entrySlot].Amplitude > ampBeforeMerge,
            $"Saturated pool must coalesce fresh kick into entry slot: before={ampBeforeMerge:F3}, after={packets[entrySlot].Amplitude:F3}");

        LMP.UI.Controls.AudioWaveBorder.EvaluateRailDisplacement(packets, disp, vocalAmp: 0.2f, harmAmp: 0.15f, vocalPhase: 0.5f, centroid: 0.45f);
        float maxD = float.MinValue;
        float minD = float.MaxValue;
        for (int i = 4; i < disp.Length - 4; i++)
        {
            maxD = MathF.Max(maxD, disp[i]);
            minD = MathF.Min(minD, disp[i]);
        }

        TestAssert.True(
            maxD < LMP.UI.Controls.AudioWaveBorder.MaxSafeDisplacement && (maxD - minD) > 1.20f,
            $"Dense chorus rail must keep strong visible excursion without clipping: min={minD:F3}, max={maxD:F3}");

        return Task.CompletedTask;
    }

    private static async Task SkipIfForeignPublisherAsync()
    {
        // Visualizer buffer is global: live playback would overwrite levels during verification
        await Task.Delay(60).ConfigureAwait(false);
        if (AudioVisualizerBuffer.HasRecentActivity)
            TestAssert.Skip("AudioVisualizerBuffer is being fed by active playback; global state would corrupt assertions");
    }

    private static (AudioPipeline Pipeline, NullAudioBackend Backend) CreatePipeline(float[] prefill)
    {
        var backend = new NullAudioBackend();
        var ring = new LockFreeRingBuffer<float>(prefill.Length * 2);
        ring.Write(prefill);

        var pipeline = AudioPipelineTestHarness.Create(
            new BlockingAudioSource(SampleRate, Channels),
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0f),
            backend,
            ring);

        return (pipeline, backend);
    }

    private static float[] CreateTone(int frames, float frequencyHz, float amplitude)
    {
        var tone = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            float s = amplitude * MathF.Sin(2f * MathF.PI * frequencyHz * f / SampleRate);
            tone[f * Channels] = s;
            tone[f * Channels + 1] = s;
        }

        return tone;
    }
}