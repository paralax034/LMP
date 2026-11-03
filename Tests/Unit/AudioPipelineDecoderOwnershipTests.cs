using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Владение декодирующим циклом <see cref="AudioPipeline"/>: таймаут остановки не порождает второго продюсера SPSC-кольца,
/// фрейм, прочитанный после отмены, не попадает в кольцо, а счётчик декодированных сэмплов равен фактически записанным.
/// Источник игнорирует отмену, как чтение, зависшее в сокете.
/// </summary>
public static class AudioPipelineDecoderOwnershipTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    /// <summary>После таймаута остановки повторный старт не запускает второй цикл поверх ещё живого первого.</summary>
    [TestMethod(TestCategory.Unit, "Decoder Stop Timeout Never Spawns A Second Loop", Group = TestGroups.Audio, Order = 16, TimeoutSeconds = 10)]
    public static async Task TestStopTimeoutKeepsSingleDecoderLoopAsync()
    {
        var source = new BlockingAudioSource(SampleRate, Channels);
        var backend = new NullAudioBackend();
        var pipeline = CreatePipeline(source, backend, new LockFreeRingBuffer<float>(SampleRate * Channels));
        try
        {
            var options = new AudioPlayerOptions();
            pipeline.StartDecoding(options, null, null);
            TestAssert.True(await AudioPipelineTestHarness.WaitUntilAsync(() => source.ReadCalls >= 1, 2_000).ConfigureAwait(false), "Decoder loop must start reading");

            // Чтение висит и игнорирует отмену: остановка обязана упереться в таймаут
            await pipeline.StopDecodingAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);

            try
            {
                pipeline.StartDecoding(options, null, null);
            }
            catch (InvalidOperationException)
            {
                // Явный отказ, пока прежний цикл жив, тоже соблюдает контракт единственного продюсера
            }

            await AudioPipelineTestHarness.WaitUntilAsync(() => source.ReadCalls >= 2, 300).ConfigureAwait(false);
            TestAssert.Equal(1, source.MaxConcurrentReads, "Only one decoder loop may read the source and write the SPSC ring");
        }
        finally
        {
            await ShutdownAsync(pipeline, source, backend).ConfigureAwait(false);
        }
    }

    /// <summary>Фрейм, который зависшее чтение вернуло уже после отмены, отбрасывается.</summary>
    [TestMethod(TestCategory.Unit, "Frame Read After Cancellation Never Reaches The Ring", Group = TestGroups.Audio, Order = 17, TimeoutSeconds = 10)]
    public static async Task TestFrameAfterCancellationIsDroppedAsync()
    {
        var source = new BlockingAudioSource(SampleRate, Channels);
        var backend = new NullAudioBackend();
        var ring = new LockFreeRingBuffer<float>(SampleRate * Channels);
        var pipeline = CreatePipeline(source, backend, ring);
        try
        {
            pipeline.StartDecoding(new AudioPlayerOptions(), null, null);
            TestAssert.True(await AudioPipelineTestHarness.WaitUntilAsync(() => source.ReadCalls >= 1, 2_000).ConfigureAwait(false), "Decoder loop must start reading");

            // Отмена уже выставлена, когда зависшее чтение наконец возвращает фрейм
            Task stop = pipeline.StopDecodingAsync(TimeSpan.FromSeconds(2));
            source.Release(AudioPipelineTestHarness.Frame());
            await stop.ConfigureAwait(false);

            TestAssert.Equal(0, ring.Count, "A frame read after cancellation must not be written into the ring");
        }
        finally
        {
            await ShutdownAsync(pipeline, source, backend).ConfigureAwait(false);
        }
    }

    /// <summary>При частичной записи в кольцо счётчик декодированных сэмплов растёт ровно на принятое кольцом.</summary>
    [TestMethod(TestCategory.Unit, "Decoded Sample Counter Matches Ring Writes On Partial Write", Group = TestGroups.Audio, Order = 18, TimeoutSeconds = 10)]
    public static async Task TestDecodedCounterMatchesPartialWriteAsync()
    {
        const int framesPerDecode = 4_096;

        var source = new BlockingAudioSource(SampleRate, Channels);
        var backend = new NullAudioBackend();

        // Кольцо вмещает 1023 сэмпла, а декодер отдаёт 8192 при заявленном MaxFrameSize 64: Write примет лишь часть
        var ring = new LockFreeRingBuffer<float>(1_024);
        var pipeline = AudioPipelineTestHarness.Create(
            source,
            new ConstantDecoder(SampleRate, Channels, framesPerDecode, maxFrameSize: 64, value: 0.1f),
            backend,
            ring);
        try
        {
            source.Release(AudioPipelineTestHarness.Frame());
            pipeline.StartDecoding(new AudioPlayerOptions(), null, null);

            TestAssert.True(
                await AudioPipelineTestHarness.WaitUntilAsync(() => ReadDecodedSamples(pipeline) > 0, 2_000).ConfigureAwait(false),
                "Decoder must process the released frame");

            long decoded = ReadDecodedSamples(pipeline);
            TestAssert.Equal((long)ring.Count, decoded,
                $"Decoded counter must equal samples accepted by the ring: ring={ring.Count}, counter={decoded}, produced={framesPerDecode * Channels}");
        }
        finally
        {
            await ShutdownAsync(pipeline, source, backend).ConfigureAwait(false);
        }
    }

    private static AudioPipeline CreatePipeline(BlockingAudioSource source, NullAudioBackend backend, LockFreeRingBuffer<float> ring) =>
        AudioPipelineTestHarness.Create(
            source,
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0.1f),
            backend,
            ring);

    private static long ReadDecodedSamples(AudioPipeline pipeline) =>
        Interlocked.Read(ref AudioPipelineTestHarness.DecodedSamples(pipeline));

    private static async Task ShutdownAsync(AudioPipeline pipeline, BlockingAudioSource source, NullAudioBackend backend)
    {
        // Отпускаем зависшие чтения, чтобы осиротевший цикл завершился до освобождения кольца
        source.Open();
        await AudioPipelineTestHarness.WaitUntilAsync(() => source.ActiveReads == 0, 1_000).ConfigureAwait(false);
        await pipeline.StopDecodingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        backend.Dispose();
    }
}