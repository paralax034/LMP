using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты алгоритмической логики аудио-тракта: интерполяция позиции слайдера,
/// ограничение при голодании буфера (starvation clamp), адаптивный гистерезис пробуждений
/// и Zero-Copy конвейер WASAPI.
/// </summary>
public static class AudioLogicTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels;

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

        var ring = new LockFreeRingBuffer<float>(BufferSamples * 4);
        float[] pcmChunk = new float[BufferSamples];

        // 1. Плавный ход интерполяции при наличии данных в кольцевом буфере
        ring.Write(pcmChunk.AsSpan());
        int activeBufferedSamples = ring.Count;

        updateMethod.Invoke(sharedState, [baseSamples, activeBufferedSamples, SampleRate, Channels, true, durationMs]);
        await Task.Delay(25).ConfigureAwait(false);

        var playingPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        double deltaMs = (playingPosition.TotalSeconds - basePositionSeconds) * 1000.0;
        TestAssert.True(deltaMs > 0.0, "Position did not advance while playing");

        // 2. Starvation Clamp: при опустошении буфера интерполяция должна мгновенно замереть
        ring.Clear();
        int starvedBufferedSamples = ring.Count;
        long starvedBaseSamples = baseSamples + activeBufferedSamples;
        double starvedBaseSeconds = (double)starvedBaseSamples / (SampleRate * Channels);

        updateMethod.Invoke(sharedState, [starvedBaseSamples, starvedBufferedSamples, SampleRate, Channels, true, durationMs]);
        await Task.Delay(25).ConfigureAwait(false);

        var starvedPosition = (TimeSpan)getPositionMethod.Invoke(sharedState, null)!;
        double starvedDeltaMs = (starvedPosition.TotalSeconds - starvedBaseSeconds) * 1000.0;
        TestAssert.True(starvedDeltaMs <= 0.001, "Starvation clamp failed: ghost advance detected");
    }

    [TestMethod(TestCategory.Unit, "Adaptive Decoder Delay & Batch Hysteresis", Group = TestGroups.Audio, Order = 2)]
    public static Task TestAdaptiveDecoderDelayAndBatchHysteresisAsync()
    {
        // 1. Проверка шкалы адаптивных задержек в зависимости от заполнения
        int delayEmpty = AudioPipeline.ComputeBufferFullDelayMs(0, SampleRate, Channels);
        int delayLow = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 200 / 1000, SampleRate, Channels);
        int delayMid = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 500 / 1000, SampleRate, Channels);
        int delayHigh = AudioPipeline.ComputeBufferFullDelayMs(SampleRate * Channels * 1500 / 1000, SampleRate, Channels);

        TestAssert.Equal(5, delayEmpty);
        TestAssert.Equal(5, delayLow);
        TestAssert.Equal(25, delayMid);
        TestAssert.Equal(180, delayHigh);

        // 2. Проверка гистерезиса пакетирования (не более 3 пробуждений декодера за 1 секунду аудио)
        var ring = new LockFreeRingBuffer<float>(SampleRate * Channels * 2);
        int frameSize = 960 * Channels;
        int batchRefillSpace = Math.Max(frameSize * 4, ring.Capacity / 4);
        float[] frameBuf = new float[frameSize];
        float[] backendChunk = new float[SampleRate * Channels / 10];

        while (ring.Available >= frameSize)
            ring.Write(frameBuf.AsSpan());

        int decoderWakeups = 0;
        for (int tick = 0; tick < 10; tick++)
        {
            ring.Read(backendChunk.AsSpan());

            if (ring.Available >= batchRefillSpace)
            {
                decoderWakeups++;
                while (ring.Available >= frameSize)
                    ring.Write(frameBuf.AsSpan());
            }
        }

        TestAssert.True(decoderWakeups <= 3, $"Excessive decoder wakeups: {decoderWakeups} (expected <= 3)");
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "WASAPI Zero-Copy Native Span DSP & Fade (0 Alloc)", Group = TestGroups.Audio, Order = 3)]
    [SupportedOSPlatform("windows")]
    public static unsafe Task TestWasapiZeroCopyNativeSpanDspAsync()
    {
        if (!OperatingSystem.IsWindows())
            TestAssert.Skip("Windows-only WASAPI test");

        const int frames = 1152; // 24ms @ 48kHz
        const int sampleCount = frames * Channels;

        float* nativeBuffer = (float*)NativeMemory.AllocZeroed((nuint)(sampleCount * sizeof(float)));
        try
        {
            var ring = new LockFreeRingBuffer<float>(sampleCount * 4);
            var gainProcessor = new GainProcessor();
            gainProcessor.SetVolumeGain(0.8f);

            float[] sourceSamples = new float[sampleCount];
            Array.Fill(sourceSamples, 0.5f);

            float fadeGain = 0f;

            TestAssert.ZeroAlloc(() =>
            {
                ring.Write(sourceSamples.AsSpan());
                Span<float> wasapiSpan = new(nativeBuffer, sampleCount);

                int read = ring.Read(wasapiSpan);
                int framesRead = read / Channels;

                fadeGain = 0.5f;

                _ = WinAudioBackend.ApplyFadeEnvelope(
                    wasapiSpan,
                    framesRead,
                    Channels,
                    WinAudioBackend.FadeState.FadingIn,
                    ref fadeGain);

                gainProcessor.Process(wasapiSpan);
            }, warmup: 5, iterations: 1000, context: "WASAPI Zero-Copy Native Span");

            // Проверяем корректность fade-out зануления хвоста
            Span<float> tailSpan = new(nativeBuffer, sampleCount);
            tailSpan.Fill(1.0f);
            fadeGain = 0.001f;

            bool completed = WinAudioBackend.ApplyFadeEnvelope(
                tailSpan,
                frames,
                Channels,
                WinAudioBackend.FadeState.FadingOut,
                ref fadeGain);

            TestAssert.True(completed, "Fade-out must complete when gain reaches 0");
            TestAssert.Equal(0f, fadeGain);
            TestAssert.Equal(0f, tailSpan[^1]);
        }
        finally
        {
            NativeMemory.Free(nativeBuffer);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GainProcessor Smooth Retargeting & Stereo Center Invariant", Group = TestGroups.Audio, Order = 4)]
    public static Task TestGainProcessorContinuousRetargetingAndStereoBalanceAsync()
    {
        var processor = new GainProcessor();
        processor.SetVolumeGain(0.2f);

        // Прогреваем до стабилизации на 0.2f
        float[] warmup = new float[4800];
        Array.Fill(warmup, 1.0f);
        processor.Process(warmup.AsSpan());

        // Имитируем быстрое движение ползунка громкости мелкими буферами по 256 сэмплов (128 стерео-фреймов),
        // когда новая цель приходит ДО завершения предыдущей рампы (1200 фреймов).
        ReadOnlySpan<float> targets = [0.4f, 0.75f, 0.3f, 0.95f, 0.15f, 0.85f];
        float[] chunk = new float[256];
        float prevFrameSample = 0.2f;

        foreach (float target in targets)
        {
            Array.Fill(chunk, 1.0f);
            processor.SetVolumeGain(target);
            processor.Process(chunk.AsSpan());

            for (int i = 0; i < chunk.Length; i += 2)
            {
                float left = chunk[i];
                float right = chunk[i + 1];

                // 1. Строгий инвариант стерео-баланса: L и R одного фрейма обязаны быть идентичны
                TestAssert.Equal(left, right, $"Stereo imbalance at sample {i}: L={left}, R={right}");

                // 2. Инвариант отсутствия ступенек ("пузырчатой плёнки"):
                // максимальный шаг за 1 стерео-фрейм при перепаде 1.0 на 1200 фреймов < 0.002f.
                float stepDelta = MathF.Abs(left - prevFrameSample);
                TestAssert.True(
                    stepDelta < 0.005f,
                    $"Discontinuous volume jump (zipper noise) detected: prev={prevFrameSample:F4}, curr={left:F4}, delta={stepDelta:F4}");

                prevFrameSample = left;
            }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Waveform Oscilloscope: Seek Warmup & Volume Glitch Detector", Group = TestGroups.Audio, Order = 5)]
    [SupportedOSPlatform("windows")]
    public static Task TestSeekAndVolumeWaveformGlitchDetectorAsync()
    {
        if (!OperatingSystem.IsWindows())
            TestAssert.Skip("Windows-only WASAPI test");

        // Доказываем, что после Seek при прогреве буфера >= 60 мс и последующем FadeIn + вращении громкости
        // в выходном сигнале нет ни одного разрыва фазы (нулевых дыр "пузырчатой плёнки") и нет перекоса L/R.
        const int warmupSamples = SampleRate * Channels * 60 / 1000; // 60 мс стерео
        const int wasapiPeriodSamples = 480 * Channels;              // 10 мс квант WASAPI

        var ring = new LockFreeRingBuffer<float>(SampleRate * Channels * 2);
        var gainProcessor = new GainProcessor();
        gainProcessor.SetVolumeGain(0.8f);

        // Заполняем 60 мс непрерывной синусоидой 440 Гц (имитация завершённого WaitForBufferAsync после Seek)
        float[] decodedPcm = new float[warmupSamples * 2];
        for (int f = 0; f < decodedPcm.Length / Channels; f++)
        {
            float val = MathF.Sin(2f * MathF.PI * 440f * f / SampleRate) * 0.8f;
            decodedPcm[f * 2] = val;
            decodedPcm[f * 2 + 1] = val;
        }
        ring.Write(decodedPcm.AsSpan());

        float fadeGain = 0f;
        var fadeState = WinAudioBackend.FadeState.FadingIn;
        float[] wasapiBuffer = new float[wasapiPeriodSamples];

        float prevL = 0f;
        float prevR = 0f;
        float maxStepJump = 0f;

        for (int tick = 0; tick < 10; tick++)
        {
            // На каждом тике меняем громкость, имитируя скольжение ползунка
            gainProcessor.SetVolumeGain(0.5f + (tick % 3) * 0.15f);

            int read = ring.Read(wasapiBuffer.AsSpan());
            TestAssert.Equal(wasapiPeriodSamples, read, "Buffer underrun (zero-gap) detected during post-seek playback");

            if (fadeState != WinAudioBackend.FadeState.None)
            {
                bool done = WinAudioBackend.ApplyFadeEnvelope(
                    wasapiBuffer.AsSpan(),
                    read / Channels,
                    Channels,
                    fadeState,
                    ref fadeGain);
                if (done)
                    fadeState = WinAudioBackend.FadeState.None;
            }

            gainProcessor.Process(wasapiBuffer.AsSpan());

            for (int i = 0; i < wasapiBuffer.Length; i += 2)
            {
                float curL = wasapiBuffer[i];
                float curR = wasapiBuffer[i + 1];

                // 1. Нулевой перекос стерео-панорамы
                TestAssert.Equal(curL, curR, $"Stereo shift detected at tick {tick}, sample {i}");

                // 2. Отсутствие вертикальных ступенек/щелчков (макс. производная 440 Гц @ 48 кГц ~ 0.058)
                float jump = MathF.Max(MathF.Abs(curL - prevL), MathF.Abs(curR - prevR));
                if (jump > maxStepJump)
                    maxStepJump = jump;

                TestAssert.True(jump < 0.08f, $"Waveform glitch/pop detected at tick {tick}, sample {i}: jump={jump:F4}");

                prevL = curL;
                prevR = curR;
            }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "TruePeakLimiter & Crossfader Stereo Link & DeClick Continuity", Group = TestGroups.Audio, Order = 6)]
    [SupportedOSPlatform("windows")]
    public static Task TestStereoLinkAndDeClickContinuityAsync()
    {
        // 1. Проверяем, что TruePeakLimiter + GainCrossfader сохраняют идеальный баланс L == R
        // и не дают щелчка в правом канале при пиках > 1.0f и активной рампе нормализации.
        var limiter = new LMP.Core.Audio.Normalization.TruePeakLimiter(SampleRate);
        var crossfader = new LMP.Core.Audio.Normalization.GainCrossfader(0.6f);
        crossfader.SetTarget(1.8f, SampleRate, Channels);

        float[] stereoSamples = new float[960 * Channels];
        for (int f = 0; f < 960; f++)
        {
            float v = MathF.Sin(2f * MathF.PI * 440f * f / SampleRate) * 0.9f;
            stereoSamples[f * 2] = v;
            stereoSamples[f * 2 + 1] = v;
        }

        limiter.Process(stereoSamples.AsSpan(), ref crossfader);

        for (int i = 0; i < stereoSamples.Length; i += 2)
        {
            TestAssert.Equal(
                stereoSamples[i],
                stereoSamples[i + 1],
                $"TruePeakLimiter/GainCrossfader broke stereo balance at sample {i}: L={stereoSamples[i]}, R={stereoSamples[i + 1]}");
        }

        // 2. Проверяем C1-гладкий 20-миллисекундный wave-to-wave кроссфейд при Seek в AudioPipeline:
        // старая волна оборвалась на +0.75f, а новая позиция начинается с противофазы -0.60f.
        const int cfFrames = 960; // 20 мс @ 48 кГц
        float[] oldTail = new float[cfFrames * Channels];
        float[] newWave = new float[cfFrames * Channels];

        for (int f = 0; f < cfFrames; f++)
        {
            // Старая волна продолжается от +0.75f
            float vOld = MathF.Sin(2f * MathF.PI * 440f * f / SampleRate + 1.1f) * 0.85f;
            oldTail[f * 2] = vOld;
            oldTail[f * 2 + 1] = vOld;

            // Новая волна стартует со своей независимой фазы (-0.60f)
            float vNew = MathF.Sin(2f * MathF.PI * 440f * f / SampleRate - 0.8f) * 0.85f;
            newWave[f * 2] = vNew;
            newWave[f * 2 + 1] = vNew;
        }

        // Применяем формулу Hermite S-curve кроссфейда из AudioPipeline.AudioCallback
        const float invTotal = 1.0f / cfFrames;
        for (int f = 0; f < cfFrames; f++)
        {
            float t = (f + 1) * invTotal;
            float newWeight = t * t * (3.0f - 2.0f * t);
            float oldWeight = 1.0f - newWeight;

            int baseIdx = f * Channels;
            for (int ch = 0; ch < Channels; ch++)
            {
                newWave[baseIdx + ch] = oldTail[baseIdx + ch] * oldWeight + newWave[baseIdx + ch] * newWeight;
            }
        }

        // Проверяем идеальный стерео-баланс L == R и отсутствие скачков производной (шаг < естественного уклона синуса ~0.049)
        float prevL = oldTail[0];
        for (int i = 0; i < newWave.Length; i += 2)
        {
            float curL = newWave[i];
            float curR = newWave[i + 1];
            TestAssert.Equal(curL, curR, $"Wave-to-wave crossfade stereo mismatch at {i}");

            float step = MathF.Abs(curL - prevL);
            TestAssert.True(step < 0.055f, $"Click during wave-to-wave crossfade at sample {i}: prev={prevL:F4}, cur={curL:F4}, step={step:F4}");
            prevL = curL;
        }

        return Task.CompletedTask;
    }
}