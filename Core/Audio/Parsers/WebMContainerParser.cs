using System.Buffers;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Адаптер <see cref="WebMParser"/> для <see cref="IContainerParser"/>.
/// Инкапсулирует логику управления памятью (Zero-Allocation) для аудио-фреймов.
/// </summary>
public sealed class WebMContainerParser : IContainerParser
{
    private readonly WebMParser _parser;
    private bool _disposed;

    // Хранит ссылку на арендованный буфер текущего фрейма, чтобы вернуть его при чтении следующего
    private IMemoryOwner<byte>? _currentFrameOwner;

    public long DurationMs => _parser.DurationMs;
    public AudioCodec Codec => AudioCodec.Opus;
    public byte[]? DecoderConfig => _parser.CodecPrivate;
    public int SampleRate => _parser.SampleRate > 0 ? _parser.SampleRate : 48000;
    public int Channels => _parser.Channels > 0 ? _parser.Channels : 2;

    /// <summary>
    /// Произошёл ли resync в текущем парсере.
    /// Если true, последующий EOF означает реальное усечение файла.
    /// </summary>
    public bool ResyncOccurred => _parser.ResyncOccurred;

    public WebMContainerParser(Stream stream)
    {
        _parser = new WebMParser(stream);
    }

    public ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        return _parser.ParseHeadersAsync(ct);
    }

    /// <summary>
    /// Читает следующий аудио-фрейм. Использует синхронный fast-path без аллокации async state machine,
    /// когда блок доступен из внутреннего буфера парсера мгновенно.
    /// </summary>
    public ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        ReleaseCurrentFrame();

        var blockTask = _parser.ReadNextBlockAsync(ct);
        if (blockTask.IsCompletedSuccessfully)
        {
            return new ValueTask<AudioFrame?>(WrapBlock(blockTask.Result));
        }

        return ReadNextFrameSlowAsync(blockTask);
    }

    private async ValueTask<AudioFrame?> ReadNextFrameSlowAsync(ValueTask<WebMParser.AudioBlock?> blockTask)
    {
        var block = await blockTask.ConfigureAwait(false);
        return WrapBlock(block);
    }

    private AudioFrame? WrapBlock(WebMParser.AudioBlock? block)
    {
        if (block == null)
            return null;

        _currentFrameOwner = block.Value.Owner;

        return new AudioFrame
        {
            Data = _currentFrameOwner.Memory[..block.Value.Length],
            TimestampMs = block.Value.TimestampMs,
            DurationMs = 20, // Стандартная длительность Opus-фрейма
            IsKeyFrame = block.Value.IsKeyFrame
        };
    }

    /// <summary>
    /// Ищет ближайшую точку входа для seek.
    /// </summary>
    /// <returns>
    /// BytePosition — абсолютная позиция в потоке (начало Cluster).
    /// TimestampMs — РЕАЛЬНЫЙ timestamp из cue point (не target!).
    /// Это позиция откуда начнётся декодирование. Decoder пропустит фреймы
    /// до targetMs через skip frames mechanism.
    /// </returns>
    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        var cuePoints = _parser.CuePoints;
        if (cuePoints.Count == 0)
            return null;

        // Бинарный поиск ближайшего cue point ≤ targetMs
        int low = 0, high = cuePoints.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (cuePoints[mid].TimeMs <= targetMs)
                low = mid;
            else
                high = mid - 1;
        }

        var cue = cuePoints[low];
        long bytePos = _parser.FindSeekPosition(targetMs) ?? 0;

        // Возвращаем РЕАЛЬНЫЙ timestamp из cue point.
        // CachingStreamSource.SeekAsync установит positionMs = cue.TimeMs,
        // и AudioPipeline.SetDecodedSamplesPosition пересчитает sample position.
        return (bytePos, cue.TimeMs);
    }

    public void Reset()
    {
        ReleaseCurrentFrame();
        _parser.Reset();
    }

    private void ReleaseCurrentFrame()
    {
        _currentFrameOwner?.Dispose();
        _currentFrameOwner = null;
    }

    public void RequireResync()
    {
        ReleaseCurrentFrame();
        _parser.RequireResync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseCurrentFrame();
        _parser.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}