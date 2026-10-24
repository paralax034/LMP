using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты алгоритмической логики аудио-тракта: интерполяция позиции слайдера,
/// ограничение при голодании буфера (starvation clamp) и адаптивный гистерезис пробуждений.
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
}