using System.Buffers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Парсер MP4/M4A контейнера для извлечения AAC фреймов.
/// Поддерживает обычный MP4 и Fragmented MP4 (fMP4).
/// </summary>
/// <remarks>
/// <para><b>Архитектура (Partial):</b></para>
/// <list type="bullet">
///   <item><c>Mp4ContainerParser.cs</c> — управление состоянием, I/O и чтение фреймов.</item>
///   <item><c>Mp4ContainerParser.Stbl.cs</c> — логика индексации обычных MP4 файлов.</item>
///   <item><c>Mp4ContainerParser.Fragment.cs</c> — логика работы с фрагментированным MP4 (Dash/HLS).</item>
/// </list>
/// <para><b>Performance:</b> Использует <see cref="Mp4BinaryReader"/> для zero-alloc парсинга заголовков
/// и переиспользуемый буфер из <see cref="ArrayPool{T}.Shared"/> для чтения фреймов без аллокаций в куче.</para>
/// </remarks>
public sealed partial class Mp4ContainerParser : IContainerParser
{
    private const int InitialFrameBufferSize = 4096;

    private readonly Mp4BinaryReader _reader;

    private long _durationMs;
    private byte[]? _decoderConfig;
    private int _sampleRate;
    private int _channels;

    // Состояние для обычного MP4 (см. partial Stbl)
    private List<SampleInfo> _samples = [];
    private int _currentSampleIndex;

    // Состояние для fMP4 (см. partial Fragment)
    private bool _isFragmented;
    private uint _trackTimeScale = 1;
    private uint _defaultSampleDuration;
    private uint _defaultSampleSize;
    private readonly Queue<SampleInfo> _fragmentSamples = new();
    private readonly List<SegmentInfo> _segments = [];
    private long _lastMdatDataStart;
    private long _lastMdatDataEnd;

    private bool _disposed;
    private byte[]? _frameBuffer;

    public long DurationMs => _durationMs;
    public AudioCodec Codec => AudioCodec.Aac;
    public byte[]? DecoderConfig => _decoderConfig;
    public int SampleRate => _sampleRate > 0 ? _sampleRate : 44100;
    public int Channels => _channels > 0 ? _channels : 2;

    public Mp4ContainerParser(Stream stream)
    {
        _reader = new Mp4BinaryReader(stream ?? throw new ArgumentNullException(nameof(stream)));
    }

    /// <summary>
    /// Выполняет первичный парсинг заголовков контейнера и строит индекс семплов.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>
    /// <c>true</c>, если контейнер успешно распознан и содержит достаточно метаданных
    /// для чтения фреймов; иначе <c>false</c>.
    /// </returns>
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        try
        {
            _reader.Position = 0;

            bool foundMoov = false;
            bool foundMoof = false;
            bool foundSidx = false;

            while (_reader.Remaining >= 8)
            {
                ct.ThrowIfCancellationRequested();

                long boxStart = _reader.Position;
                var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
                if (header.IsEmpty)
                    break;

                long boxEnd = boxStart + header.Size;

                switch (header.Type)
                {
                    case Mp4BinaryReader.FCC_MOOV:
                        await ParseMoovAsync(boxEnd, ct).ConfigureAwait(false);
                        foundMoov = true;
                        break;

                    case Mp4BinaryReader.FCC_SIDX:
                        await ParseSidxAsync(boxEnd, ct).ConfigureAwait(false);
                        foundSidx = true;
                        break;

                    case Mp4BinaryReader.FCC_MOOF:
                        _isFragmented = true;
                        foundMoof = true;

                        if (_decoderConfig != null)
                            await ParseMoofAsync(boxStart, boxEnd, ct).ConfigureAwait(false);
                        else
                            await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
                        break;

                    default:
                        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
                        break;
                }

                if (foundMoov && !_isFragmented && _samples.Count > 0)
                    break;

                if (foundMoov && _isFragmented && (foundSidx || foundMoof))
                    break;
            }

            if (_isFragmented)
                return await FinalizeFragmentedInitAsync(foundSidx, ct).ConfigureAwait(false);

            if (_samples.Count == 0)
            {
                Log.Error("[Mp4Parser] No samples found");
                return false;
            }

            Log.Debug($"[Mp4Parser] Regular MP4: {_samples.Count} samples, duration={_durationMs}ms");
            return true;
        }
        catch (Exception ex) when (Helpers.CancellationHelper.IsCancellationLike(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[Mp4Parser] Parse failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Читает следующий аудио-фрейм. При наличии семпла в таблице и синхронном чтении из потока
    /// возвращает результат мгновенно без аллокаций в куче.
    /// </summary>
    public ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        if (_isFragmented)
        {
            if (_fragmentSamples.Count > 0)
                return ReadSampleAsFrameAsync(_fragmentSamples.Dequeue(), ct);

            return ReadNextFragmentedFrameAsync(ct);
        }

        if (_currentSampleIndex >= _samples.Count)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        return ReadSampleAsFrameAsync(_samples[_currentSampleIndex++], ct);
    }

    private Memory<byte> RentFrameMemory(int requiredSize)
    {
        var buf = _frameBuffer;
        if (buf == null || buf.Length < requiredSize)
        {
            if (buf != null)
                ArrayPool<byte>.Shared.Return(buf);

            buf = ArrayPool<byte>.Shared.Rent(Math.Max(requiredSize, InitialFrameBufferSize));
            _frameBuffer = buf;
        }

        return buf.AsMemory(0, requiredSize);
    }

    /// <summary>
    /// Считывает сэмпл MP4 в переиспользуемый буфер. При отмене операции возвращает null,
    /// предотвращая ложный <see cref="ParserCorruptionException"/>.
    /// </summary>
    private ValueTask<AudioFrame?> ReadSampleAsFrameAsync(SampleInfo sample, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        _reader.Position = sample.Offset;
        var memory = RentFrameMemory(sample.Size);

        try
        {
            var readTask = _reader.ReadExactlyAsync(memory, ct);
            if (readTask.IsCompletedSuccessfully)
            {
                return new ValueTask<AudioFrame?>(new AudioFrame
                {
                    Data = memory,
                    TimestampMs = sample.TimestampMs,
                    DurationMs = sample.DurationMs,
                    IsKeyFrame = sample.IsKeyFrame
                });
            }

            return ReadSampleAsFrameSlowAsync(readTask, memory, sample, ct);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || Helpers.CancellationHelper.IsCancellationLike(ex))
                return new ValueTask<AudioFrame?>((AudioFrame?)null);

            throw new ParserCorruptionException(sample.Offset, $"Failed to read MP4 sample at offset {sample.Offset}", ex);
        }
    }

    private static async ValueTask<AudioFrame?> ReadSampleAsFrameSlowAsync(
        ValueTask readTask,
        Memory<byte> memory,
        SampleInfo sample,
        CancellationToken ct)
    {
        try
        {
            await readTask.ConfigureAwait(false);

            return new AudioFrame
            {
                Data = memory,
                TimestampMs = sample.TimestampMs,
                DurationMs = sample.DurationMs,
                IsKeyFrame = sample.IsKeyFrame
            };
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || Helpers.CancellationHelper.IsCancellationLike(ex))
                return null;

            throw new ParserCorruptionException(sample.Offset, $"Failed to read MP4 sample at offset {sample.Offset}", ex);
        }
    }

    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        if (_isFragmented) return FindSeekPositionFragmented(targetMs);
        if (_samples.Count == 0) return null;

        int index = BinarySearchSample(targetMs);
        _currentSampleIndex = index;
        return (_samples[index].Offset, _samples[index].TimestampMs);
    }

    public void Reset()
    {
        _currentSampleIndex = 0;
        _fragmentSamples.Clear();
    }

    private void ReleaseFrameBuffer()
    {
        var buf = _frameBuffer;
        if (buf != null)
        {
            _frameBuffer = null;
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <inheritdoc/>
    public void RequireResync()
    {
        long currentPos = _reader.Position;

        if (_isFragmented)
        {
            _fragmentSamples.Clear();
        }
        else
        {
            // Для классического MP4 сдвигаем индекс к первому сэмплу после текущей физической позиции
            while (_currentSampleIndex < _samples.Count && _samples[_currentSampleIndex].Offset < currentPos)
            {
                _currentSampleIndex++;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseFrameBuffer();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // Вспомогательные структуры
    private record struct SampleInfo(long Offset, int Size, long TimestampMs, int DurationMs, bool IsKeyFrame);
    private record struct SegmentInfo(long ByteOffset, long TimeOffset, uint Duration, uint Size);
}