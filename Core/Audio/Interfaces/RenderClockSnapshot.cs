namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// A consistent snapshot of the render clock published by the device thread.
/// </summary>
/// <param name="PlayedSamples">Source samples (frames × channels) actually rendered by the <c>Timestamp</c> moment.</param>
/// <param name="QueuedSamples">Source samples that the device will play sequentially after <c>PlayedSamples</c>, without underrun silence ahead; the upper extrapolation bound.</param>
/// <param name="Timestamp">The point in time when readings were taken, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
/// <param name="SamplesPerSecond">The clock rate: source sample rate × number of channels.</param>
internal readonly record struct RenderClockSnapshot(long PlayedSamples, int QueuedSamples, long Timestamp, int SamplesPerSecond);