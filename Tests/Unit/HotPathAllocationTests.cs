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
public static class HotPathAllocationTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    /// <summary>100 мс при 48 kHz stereo = 9600 float.</summary>
    private const int BufferSamples = SampleRate / 10 * Channels;
    private const int WarmupIterations = 5;
    private const int MeasureIterations = 2_000;

    // helpers

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

    // LockFreeRingBuffer

    [TestMethod(TestCategory.Unit, "RingBuffer Write: 0 alloc", Group = TestGroups.Audio, Order = 10)]
    public static Task TestRingBufferWriteZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] data = CreateSineBuffer(BufferSamples);

        long allocated = MeasureAllocations(() =>
        {
            ring.Write(data.AsSpan());
            // Дренируем чтобы Write не упирался в Available
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

    // Full AudioCallback simulation

    [TestMethod(TestCategory.Unit, "Full AudioCallback sim: 0 alloc", Group = TestGroups.Audio, Order = 20)]
    public static Task TestAudioCallbackSimulationZeroAllocAsync()
    {
        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        var processor = new GainProcessor();
        processor.SetVolumeGain(0.8f);

        float[] src = CreateSineBuffer(BufferSamples);
        float[] dst = new float[BufferSamples];

        // Прогрев: ramp должен завершиться
        for (int i = 0; i < 3000; i++)
        {
            ring.Write(src.AsSpan());
            int read = ring.Read(dst.AsSpan());
            if (read > 0) processor.Process(dst.AsSpan(0, read));
        }

        long allocated = MeasureAllocations(() =>
        {
            // Producer: декодер пишет в ring
            ring.Write(src.AsSpan());

            // Consumer: AudioCallback читает из ring и применяет gain
            int read = ring.Read(dst.AsSpan());
            if (read > 0)
                processor.Process(dst.AsSpan(0, read));
        });

        Log.Info($"[HotPath] Full AudioCallback simulation: {allocated} B / {MeasureIterations} calls");
        AssertZeroAlloc(allocated, "Full AudioCallback simulation");

        return Task.CompletedTask;
    }
}