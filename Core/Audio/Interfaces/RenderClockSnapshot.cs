namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Согласованный снимок часов рендера, опубликованный потоком устройства.
/// </summary>
/// <param name="PlayedSamples">Сэмплы источника (frames × channels), фактически прозвучавшие к моменту <c>Timestamp</c>.</param>
/// <param name="QueuedSamples">Сэмплы источника, которые устройство проиграет подряд после <c>PlayedSamples</c>, без тишины недогруза впереди; верхняя граница экстраполяции.</param>
/// <param name="Timestamp">Момент снятия показаний в тиках <see cref="System.Diagnostics.Stopwatch"/>.</param>
/// <param name="SamplesPerSecond">Скорость хода часов: частота источника × число каналов.</param>
public readonly record struct RenderClockSnapshot(long PlayedSamples, int QueuedSamples, long Timestamp, int SamplesPerSecond);