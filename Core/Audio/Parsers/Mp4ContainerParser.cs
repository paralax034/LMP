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
/// и <see cref="MemoryPool{T}"/> для zero-alloc чтения фреймов.</para>
/// </remarks>
public sealed partial class Mp4ContainerParser : IContainerParser
{
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
    private IMemoryOwner<byte>? _currentFrameOwner;

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

    public async ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        ReleaseCurrentFrame();

        if (_isFragmented)
            return await ReadNextFragmentedFrameAsync(ct).ConfigureAwait(false);

        if (_currentSampleIndex >= _samples.Count)
            return null;

        return await ReadSampleAsFrameAsync(_samples[_currentSampleIndex++], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Считывает сэмпл MP4. При отмене операции или смене позиции возвращает null,
    /// предотвращая ложный ParserCorruptionException.
    /// </summary>
    private async ValueTask<AudioFrame?> ReadSampleAsFrameAsync(SampleInfo sample, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        _reader.Position = sample.Offset;
        var owner = MemoryPool<byte>.Shared.Rent(sample.Size);
        var memory = owner.Memory[..sample.Size];

        try
        {
            await _reader.ReadExactlyAsync(memory, ct).ConfigureAwait(false);
            _currentFrameOwner = owner;

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
            owner.Dispose();

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
        ReleaseCurrentFrame();
    }

    private void ReleaseCurrentFrame()
    {
        _currentFrameOwner?.Dispose();
        _currentFrameOwner = null;
    }

    /// <inheritdoc/>
    public void RequireResync()
    {
        ReleaseCurrentFrame();
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
        ReleaseCurrentFrame();
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