using System.Diagnostics;
using LMP.Core.Audio;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Задержка показа уровней спектра считается в частоте устройства, а не декодера.
/// </summary>
public static class SpectrumPresentationDelayTests
{
    /// <summary>Очередь 4800 кадров при 48 кГц даёт ровно 100 мс; вырожденные входы дают 0.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Presentation Delay Uses Device Rate", Group = TestGroups.Audio, Order = 21)]
    public static Task TestSpectrumPresentationDelayUsesDeviceRateAsync()
    {
        long tenthSecond = Stopwatch.Frequency / 10;
        long delay = AudioPipeline.ComputePresentationDelayTicks(4_800 * 2, 48_000, 2);
        TestAssert.Equal(tenthSecond, delay, "4800 device frames at 48 kHz must delay levels by exactly 100 ms");

        // Контроль: деление на частоту декодера 44.1 кГц давало заметно большую задержку
        long decoderScaled = 4_800L * 2 * Stopwatch.Frequency / (44_100L * 2);
        TestAssert.True(decoderScaled - delay > tenthSecond / 20, "Control: decoder-rate scaling must differ by more than 5 %");

        TestAssert.Equal(0L, AudioPipeline.ComputePresentationDelayTicks(0, 48_000, 2), "Empty device queue must not delay levels");
        TestAssert.Equal(0L, AudioPipeline.ComputePresentationDelayTicks(9_600, 0, 2), "Unknown device rate must not delay levels");
        TestAssert.Equal(0L, AudioPipeline.ComputePresentationDelayTicks(9_600, 48_000, 0), "Zero channels must not delay levels");
        return Task.CompletedTask;
    }
}