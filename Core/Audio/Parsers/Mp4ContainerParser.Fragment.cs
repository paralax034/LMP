using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Parsers;

public sealed partial class Mp4ContainerParser
{
    /// <summary>
    /// Результат разбора <c>tfhd</c> box.
    /// </summary>
    /// <param name="HasBaseDataOffset">Присутствует ли base data offset.</param>
    /// <param name="BaseDataOffset">Абсолютный base data offset.</param>
    /// <param name="DefaultBaseIsMoof">Используется ли moof как база по умолчанию.</param>
    /// <param name="HasDefaultSampleDuration">Присутствует ли default sample duration.</param>
    /// <param name="DefaultSampleDuration">Значение default sample duration.</param>
    /// <param name="HasDefaultSampleSize">Присутствует ли default sample size.</param>
    /// <param name="DefaultSampleSize">Значение default sample size.</param>
    private readonly record struct TfhdResult(
        bool HasBaseDataOffset,
        long BaseDataOffset,
        bool DefaultBaseIsMoof,
        bool HasDefaultSampleDuration,
        uint DefaultSampleDuration,
        bool HasDefaultSampleSize,
        uint DefaultSampleSize);

    /// <summary>
    /// Декодированные флаги <c>trun</c> box.
    /// </summary>
    /// <param name="Flags">Raw 24-bit flags value.</param>
    private readonly record struct TrunFlags(uint Flags)
    {
        /// <summary>Присутствует поле data_offset.</summary>
        public bool HasDataOffset => (Flags & 0x000001) != 0;

        /// <summary>Присутствует поле first_sample_flags.</summary>
        public bool HasFirstSampleFlags => (Flags & 0x000004) != 0;

        /// <summary>Каждый sample содержит duration.</summary>
        public bool HasSampleDuration => (Flags & 0x000100) != 0;

        /// <summary>Каждый sample содержит size.</summary>
        public bool HasSampleSize => (Flags & 0x000200) != 0;

        /// <summary>Каждый sample содержит flags.</summary>
        public bool HasSampleFlags => (Flags & 0x000400) != 0;

        /// <summary>Каждый sample содержит composition time offset.</summary>
        public bool HasSampleCompositionTimeOffset => (Flags & 0x000800) != 0;
    }

    /// <summary>
    /// Финализирует инициализацию fragmented MP4 после первичного scan.
    /// </summary>
    /// <param name="foundSidx">Найден ли sidx во время первичного scan.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// <c>true</c>, если инициализация завершена и decoder config доступен;
    /// иначе <c>false</c>.
    /// </returns>
    private async Task<bool> FinalizeFragmentedInitAsync(bool foundSidx, CancellationToken ct)
    {
        if (!foundSidx && _durationMs <= 0)
            await EstimateDurationFromMoofsAsync(ct).ConfigureAwait(false);

        Log.Info($"[Mp4Parser] Fragmented MP4: rate={_sampleRate}, channels={_channels}, duration={_durationMs}ms, segments={_segments.Count}");

        _reader.Position = 0;
        await ScanToFirstMoofAsync(ct).ConfigureAwait(false);

        return _decoderConfig != null;
    }

    /// <summary>
    /// Сканирует поток до первого <c>moof</c> box для старта чтения fragmented data.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    private async Task ScanToFirstMoofAsync(CancellationToken ct)
    {
        while (_reader.Remaining >= 8)
        {
            long boxStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            if (header.Type == Mp4BinaryReader.FCC_MOOF)
            {
                _reader.Position = boxStart;
                return;
            }

            await _reader.SkipToAsync(boxStart + header.Size, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Разбирает <c>sidx</c> box и строит seek map сегментов.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseSidxAsync(long boxEnd, CancellationToken ct)
    {
        int version = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(3, ct).ConfigureAwait(false); // flags
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // reference_ID

        uint timescale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        if (timescale == 0)
            timescale = _trackTimeScale > 0 ? _trackTimeScale : 44100;

        long firstOffset;
        if (version == 0)
        {
            await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false); // earliest_presentation_time
            firstOffset = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false); // earliest_presentation_time
            firstOffset = (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false);
        }

        await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false); // reserved
        ushort referenceCount = await _reader.ReadUInt16BEAsync(ct).ConfigureAwait(false);

        _segments.Clear();

        long currentByteOffset = boxEnd + firstOffset;
        long totalDuration = 0;

        for (int i = 0; i < referenceCount; i++)
        {
            uint referenceInfo = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint referencedSize = referenceInfo & 0x7FFFFFFF;
            uint subsegmentDuration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // SAP info

            _segments.Add(new SegmentInfo(currentByteOffset, totalDuration, subsegmentDuration, referencedSize));

            currentByteOffset += referencedSize;
            totalDuration += subsegmentDuration;
        }

        long durationMs = totalDuration * 1000 / timescale;
        if (durationMs > _durationMs)
            _durationMs = durationMs;

        if (_trackTimeScale == 1)
            _trackTimeScale = timescale;

        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Разбирает <c>moof</c> box и извлекает sample metadata текущего фрагмента.
    /// </summary>
    /// <param name="moofStart">Абсолютная позиция начала moof.</param>
    /// <param name="moofEnd">Абсолютная позиция конца moof.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseMoofAsync(long moofStart, long moofEnd, CancellationToken ct)
    {
        _fragmentSamples.Clear();

        while (_reader.Position < moofEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_TRAF)
                await ParseTrafAsync(childEnd, moofStart, moofEnd, ct).ConfigureAwait(false);
            else
                await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Разбирает <c>traf</c> box.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца traf.</param>
    /// <param name="moofStart">Абсолютная позиция начала moof.</param>
    /// <param name="moofEnd">Абсолютная позиция конца moof.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseTrafAsync(long boxEnd, long moofStart, long moofEnd, CancellationToken ct)
    {
        uint sampleDuration = _defaultSampleDuration;
        uint sampleSize = _defaultSampleSize;
        long baseDataOffset = moofStart;
        long baseMediaDecodeTime = 0;

        long savedPosition = _reader.Position;

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_TFHD)
            {
                var tfhd = await ParseTfhdAsync(ct).ConfigureAwait(false);

                if (tfhd.HasBaseDataOffset)
                    baseDataOffset = tfhd.BaseDataOffset;
                else if (tfhd.DefaultBaseIsMoof)
                    baseDataOffset = moofStart;

                if (tfhd.HasDefaultSampleDuration)
                    sampleDuration = tfhd.DefaultSampleDuration;

                if (tfhd.HasDefaultSampleSize)
                    sampleSize = tfhd.DefaultSampleSize;

                break;
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }

        _reader.Position = savedPosition;

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_TFDT:
                    baseMediaDecodeTime = await ParseTfdtAsync(ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_TRUN:
                    await ParseTrunAsync(baseDataOffset, moofEnd, baseMediaDecodeTime, sampleDuration, sampleSize, ct).ConfigureAwait(false);
                    break;
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Разбирает <c>tfhd</c> box.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Декодированный результат tfhd.</returns>
    private async Task<TfhdResult> ParseTfhdAsync(CancellationToken ct)
    {
        uint versionFlags = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        uint flags = versionFlags & 0x00FFFFFF;

        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // track_ID

        long baseDataOffset = 0;
        uint defaultSampleDuration = 0;
        uint defaultSampleSize = 0;

        bool hasBaseDataOffset = (flags & 0x000001) != 0;
        bool hasSampleDescriptionIndex = (flags & 0x000002) != 0;
        bool hasDefaultSampleDuration = (flags & 0x000008) != 0;
        bool hasDefaultSampleSize = (flags & 0x000010) != 0;
        bool hasDefaultSampleFlags = (flags & 0x000020) != 0;
        bool defaultBaseIsMoof = (flags & 0x020000) != 0;

        if (hasBaseDataOffset)
            baseDataOffset = (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false);

        if (hasSampleDescriptionIndex)
            await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

        if (hasDefaultSampleDuration)
            defaultSampleDuration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        if (hasDefaultSampleSize)
            defaultSampleSize = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        if (hasDefaultSampleFlags)
            await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

        return new TfhdResult(
            hasBaseDataOffset,
            baseDataOffset,
            defaultBaseIsMoof,
            hasDefaultSampleDuration,
            defaultSampleDuration,
            hasDefaultSampleSize,
            defaultSampleSize);
    }

    /// <summary>
    /// Разбирает <c>tfdt</c> box и возвращает base media decode time.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Base media decode time.</returns>
    private async Task<long> ParseTfdtAsync(CancellationToken ct)
    {
        int version = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(3, ct).ConfigureAwait(false);

        return version == 1
            ? (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false)
            : await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Разбирает <c>trun</c> box и добавляет samples текущего фрагмента в очередь.
    /// </summary>
    /// <param name="baseDataOffset">Базовый offset данных.</param>
    /// <param name="moofEnd">Абсолютная позиция конца moof.</param>
    /// <param name="baseMediaDecodeTime">Базовое decode time.</param>
    /// <param name="defaultDuration">Default sample duration.</param>
    /// <param name="defaultSize">Default sample size.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseTrunAsync(
        long baseDataOffset,
        long moofEnd,
        long baseMediaDecodeTime,
        uint defaultDuration,
        uint defaultSize,
        CancellationToken ct)
    {
        uint versionFlags = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        uint version = versionFlags >> 24;
        var flags = new TrunFlags(versionFlags & 0x00FFFFFF);

        uint sampleCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        int dataOffset = 0;
        if (flags.HasDataOffset)
            dataOffset = await _reader.ReadInt32BEAsync(ct).ConfigureAwait(false);

        if (flags.HasFirstSampleFlags)
            await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

        long currentOffset = baseDataOffset + dataOffset;
        long currentTime = baseMediaDecodeTime;

        for (uint i = 0; i < sampleCount; i++)
        {
            uint duration = defaultDuration;
            uint size = defaultSize;
            uint sampleFlags = 0;
            int compositionTimeOffset = 0;

            if (flags.HasSampleDuration)
                duration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

            if (flags.HasSampleSize)
                size = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

            if (flags.HasSampleFlags)
                sampleFlags = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

            if (flags.HasSampleCompositionTimeOffset)
            {
                compositionTimeOffset = version == 0
                    ? (int)await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false)
                    : await _reader.ReadInt32BEAsync(ct).ConfigureAwait(false);
            }

            long timestampMs = _trackTimeScale > 0
                ? (currentTime + compositionTimeOffset) * 1000 / _trackTimeScale
                : 0;

            int durationMs = _trackTimeScale > 0
                ? (int)(duration * 1000 / _trackTimeScale)
                : 23;

            bool isKeyFrame = (sampleFlags & 0x00010000) == 0;

            _fragmentSamples.Enqueue(new SampleInfo(currentOffset, (int)size, timestampMs, durationMs, isKeyFrame));

            currentOffset += size;
            currentTime += duration;
        }

        await _reader.SkipToAsync(moofEnd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Сканирует поток в поисках следующего фрагмента и разбирает его.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c>, если найден и разобран следующий фрагмент.</returns>
    private async Task<bool> ScanAndParseNextFragmentAsync(CancellationToken ct)
    {
        while (_reader.Remaining >= 8)
        {
            long boxStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
            {
                if (boxStart + 4096 < _reader.Length)
                {
                    throw new ParserCorruptionException(
                        boxStart,
                        $"Zeroed/corrupted fMP4 box header at byte {boxStart} of {_reader.Length}");
                }
                return false;
            }

            long boxEnd = boxStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_MOOF:
                    await ParseMoofAsync(boxStart, boxEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_MDAT:
                    _lastMdatDataStart = _reader.Position;
                    _lastMdatDataEnd = boxEnd;

                    if (_fragmentSamples.Count > 0)
                    {
                        CorrectSampleOffsets(boxEnd);
                        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
                        return _fragmentSamples.Count > 0;
                    }

                    await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
                    break;

                default:
                    await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Корректирует относительные sample offsets относительно текущего <c>mdat</c>.
    /// </summary>
    /// <param name="moofEnd">Абсолютная позиция конца текущего moof.</param>
    private void CorrectSampleOffsets(long moofEnd)
    {
        if (_fragmentSamples.Count == 0)
            return;

        int count = _fragmentSamples.Count;
        long firstOffset = _fragmentSamples.Peek().Offset;
        long correction = firstOffset < moofEnd ? _lastMdatDataStart - firstOffset : 0;

        for (int i = 0; i < count; i++)
        {
            var sample = _fragmentSamples.Dequeue();
            long correctedOffset = sample.Offset + correction;

            if (correctedOffset >= _lastMdatDataStart &&
                correctedOffset + sample.Size <= _lastMdatDataEnd)
            {
                _fragmentSamples.Enqueue(sample with { Offset = correctedOffset });
            }
        }
    }

    /// <summary>
    /// Читает следующий фрейм из fragmented MP4.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Следующий фрейм или <c>null</c> при EOF.</returns>
    private async ValueTask<AudioFrame?> ReadNextFragmentedFrameAsync(CancellationToken ct)
    {
        while (_fragmentSamples.Count == 0)
        {
            if (_reader.Position >= _reader.Length)
                return null;

            if (!await ScanAndParseNextFragmentAsync(ct).ConfigureAwait(false))
                return null;
        }

        return await ReadSampleAsFrameAsync(_fragmentSamples.Dequeue(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Находит позицию seek по индексу сегментов fragmented MP4.
    /// </summary>
    /// <param name="targetMs">Целевая позиция в миллисекундах.</param>
    /// <returns>Byte position и timestamp сегмента, либо <c>null</c>.</returns>
    private (long BytePosition, long TimestampMs)? FindSeekPositionFragmented(long targetMs)
    {
        if (_segments.Count == 0)
            return (0, 0);

        long targetTime = targetMs * _trackTimeScale / 1000;

        int left = 0;
        int right = _segments.Count - 1;
        int result = 0;

        while (left <= right)
        {
            int mid = (left + right) / 2;
            if (_segments[mid].TimeOffset <= targetTime)
            {
                result = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        var segment = _segments[result];
        return (segment.ByteOffset, segment.TimeOffset * 1000 / _trackTimeScale);
    }

    /// <summary>
    /// Выполняет fallback-оценку длительности fragmented MP4 по moof/traf/trun.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    private async Task EstimateDurationFromMoofsAsync(CancellationToken ct)
    {
        long savedPosition = _reader.Position;
        _reader.Position = 0;

        long lastTimestamp = 0;
        long lastDuration = 0;

        try
        {
            while (_reader.Remaining >= 8)
            {
                long boxStart = _reader.Position;
                var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
                if (header.IsEmpty)
                    break;

                long boxEnd = boxStart + header.Size;

                if (header.Type == Mp4BinaryReader.FCC_MOOF)
                {
                    var (timestamp, duration) = await QuickParseMoofForTimingAsync(boxEnd, ct).ConfigureAwait(false);
                    if (timestamp >= 0)
                    {
                        _segments.Add(new SegmentInfo(boxStart, timestamp, (uint)duration, (uint)(boxEnd - boxStart)));
                        lastTimestamp = timestamp;
                        lastDuration = duration;
                    }
                }

                await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
            }

            if (lastTimestamp > 0 && _trackTimeScale > 0)
            {
                long estimatedMs = (lastTimestamp + lastDuration) * 1000 / _trackTimeScale;
                if (estimatedMs > _durationMs)
                    _durationMs = estimatedMs;
            }
        }
        finally
        {
            _reader.Position = savedPosition;
        }
    }

    /// <summary>
    /// Быстро извлекает timing из одного moof без построения полной sample queue.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца moof.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Timestamp и суммарная duration фрагмента.</returns>
    private async Task<(long Timestamp, long Duration)> QuickParseMoofForTimingAsync(long boxEnd, CancellationToken ct)
    {
        long timestamp = -1;
        long totalDuration = 0;

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_TRAF)
            {
                while (_reader.Position < childEnd)
                {
                    long trafChildStart = _reader.Position;
                    var trafHeader = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
                    if (trafHeader.IsEmpty)
                        break;

                    long trafChildEnd = trafChildStart + trafHeader.Size;

                    if (trafHeader.Type == Mp4BinaryReader.FCC_TFDT)
                        timestamp = await ParseTfdtAsync(ct).ConfigureAwait(false);
                    else if (trafHeader.Type == Mp4BinaryReader.FCC_TRUN)
                        totalDuration = await QuickParseTrunDurationAsync(ct).ConfigureAwait(false);

                    await _reader.SkipToAsync(trafChildEnd, ct).ConfigureAwait(false);
                }
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }

        return (timestamp, totalDuration);
    }

    /// <summary>
    /// Быстро суммирует duration всех samples в trun без построения sample queue.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Суммарная duration trun.</returns>
    private async Task<long> QuickParseTrunDurationAsync(CancellationToken ct)
    {
        uint versionFlags = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        var flags = new TrunFlags(versionFlags & 0x00FFFFFF);

        uint sampleCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        if (flags.HasDataOffset)
            await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

        if (flags.HasFirstSampleFlags)
            await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

        long totalDuration = 0;

        for (uint i = 0; i < sampleCount; i++)
        {
            uint duration = _defaultSampleDuration;

            if (flags.HasSampleDuration)
                duration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

            if (flags.HasSampleSize)
                await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

            if (flags.HasSampleFlags)
                await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

            if (flags.HasSampleCompositionTimeOffset)
                await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false);

            totalDuration += duration;
        }

        return totalDuration;
    }
}