namespace LMP.Core.Audio.Parsers;

internal sealed partial class Mp4ContainerParser
{
    /// <summary>
    /// Parses the <c>moov</c> box and its child elements.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseMoovAsync(long boxEnd, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();

            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_TRAK:
                    await ParseTrakAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_MVHD:
                    await ParseMvhdAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_MVEX:
                    _isFragmented = true;
                    await ParseMvexAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                default:
                    await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Parses the <c>mvhd</c> box and extracts the container duration.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseMvhdAsync(long boxEnd, CancellationToken ct)
    {
        int version = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(3, ct).ConfigureAwait(false);

        uint timescale;
        long duration;

        if (version == 1)
        {
            await _reader.SkipBytesAsync(16, ct).ConfigureAwait(false);
            timescale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            duration = (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await _reader.SkipBytesAsync(8, ct).ConfigureAwait(false);
            timescale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            duration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        }

        if (_trackTimeScale == 1 && timescale > 0)
            _trackTimeScale = timescale;

        if (duration > 0 && timescale > 0)
            _durationMs = duration * 1000 / timescale;

        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the <c>mvex</c> box and extracts default sample parameters for fMP4.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseMvexAsync(long boxEnd, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_TREX)
                await ParseTrexAsync(ct).ConfigureAwait(false);

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses the <c>trex</c> box and extracts default sample duration/size values.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseTrexAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // track_ID
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // default_sample_description_index
        _defaultSampleDuration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        _defaultSampleSize = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the <c>trak</c> box.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseTrakAsync(long boxEnd, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_MDIA)
            {
                var (isAudio, timeScale) = await ParseMdiaAsync(header.Size - header.HeaderSize, ct).ConfigureAwait(false);
                if (isAudio && _trackTimeScale == 1)
                    _trackTimeScale = timeScale;
            }
            else
            {
                await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Parses the <c>mdia</c> box, determining the audio track and its time scale.
    /// </summary>
    /// <param name="boxSize">Size of the box payload excluding the header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple indicating whether the track is an audio track and its time scale.
    /// </returns>
    private async Task<(bool IsAudio, uint TimeScale)> ParseMdiaAsync(long boxSize, CancellationToken ct)
    {
        long boxEnd = _reader.Position + boxSize;
        bool isAudio = false;
        uint timeScale = 1;

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_HDLR:
                    await _reader.SkipBytesAsync(8, ct).ConfigureAwait(false);
                    uint handlerType = await _reader.ReadFourCCAsync(ct).ConfigureAwait(false);
                    isAudio = handlerType == Mp4BinaryReader.FCC_SOUN;
                    break;

                case Mp4BinaryReader.FCC_MDHD:
                    {
                        int version = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
                        await _reader.SkipBytesAsync(3, ct).ConfigureAwait(false);
                        await _reader.SkipBytesAsync(version == 1 ? 16 : 8, ct).ConfigureAwait(false);
                        timeScale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
                        if (isAudio || _trackTimeScale == 1)
                            _trackTimeScale = timeScale;
                        break;
                    }

                case Mp4BinaryReader.FCC_MINF when isAudio:
                    await ParseMinfAsync(childEnd, timeScale, ct).ConfigureAwait(false);
                    break;
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }

        return (isAudio, timeScale);
    }

    /// <summary>
    /// Parses the <c>minf</c> box and delegates sample table parsing.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="timeScale">Time scale of the current track.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseMinfAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_STBL)
                await ParseStblAsync(childEnd, timeScale, ct).ConfigureAwait(false);
            else
                await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses the <c>stbl</c> box and collects data to build the sample table.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="timeScale">Time scale of the current track.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseStblAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        List<uint> sampleSizes = [];
        List<(uint firstChunk, uint samplesPerChunk, uint descIdx)> stsc = [];
        List<long> chunkOffsets = [];
        List<(uint count, uint delta)> stts = [];

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_STSD:
                    await ParseStsdAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STSZ:
                    sampleSizes = await ParseStszAsync(ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STSC:
                    stsc = await ParseStscAsync(ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STCO:
                    chunkOffsets = await ParseStcoAsync(false, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_CO64:
                    chunkOffsets = await ParseStcoAsync(true, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STTS:
                    stts = await ParseSttsAsync(ct).ConfigureAwait(false);
                    break;
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }

        BuildSampleTable(sampleSizes, stsc, chunkOffsets, stts, timeScale);
    }

    /// <summary>
    /// Parses the <c>stsd</c> box and extracts AAC sample entry parameters.
    /// </summary>
    /// <param name="boxEnd">Absolute end position of the box.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseStsdAsync(long boxEnd, CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint entryCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        if (entryCount == 0)
        {
            await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
            return;
        }

        long entryStart = _reader.Position;
        var entryHeader = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
        if (entryHeader.IsEmpty)
        {
            await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
            return;
        }

        long entryEnd = entryStart + entryHeader.Size;

        await _reader.SkipBytesAsync(6 + 2 + 8, ct).ConfigureAwait(false);
        _channels = await _reader.ReadUInt16BEAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(2 + 4, ct).ConfigureAwait(false);
        _sampleRate = (int)(await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false) >> 16);

        while (_reader.Position < entryEnd)
        {
            long nestedStart = _reader.Position;
            var nestedHeader = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (nestedHeader.IsEmpty)
                break;

            long nestedEnd = nestedStart + nestedHeader.Size;

            if (nestedHeader.Type == Mp4BinaryReader.FCC_ESDS)
            {
                await ParseEsdsAsync(ct).ConfigureAwait(false);
                break;
            }

            await _reader.SkipToAsync(nestedEnd, ct).ConfigureAwait(false);
        }

        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the <c>esds</c> descriptor and extracts the AAC Audio Specific Config.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task ParseEsdsAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags

        int tag = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if (tag != 0x03)
            return;

        await _reader.ReadExpandableLengthAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false); // ES_ID

        int flags = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if ((flags & 0x80) != 0)
            await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false);

        if ((flags & 0x40) != 0)
        {
            int extLen = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
            await _reader.SkipBytesAsync(extLen, ct).ConfigureAwait(false);
        }

        if ((flags & 0x20) != 0)
            await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false);

        tag = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if (tag != 0x04)
            return;

        await _reader.ReadExpandableLengthAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(13, ct).ConfigureAwait(false);

        tag = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if (tag != 0x05)
            return;

        int dsiLength = await _reader.ReadExpandableLengthAsync(ct).ConfigureAwait(false);
        if (dsiLength <= 0 || dsiLength > 64)
            return;

        byte[] fullDsi = await _reader.ReadBytesAsync(dsiLength, ct).ConfigureAwait(false);

        int realLength = dsiLength;
        while (realLength > 2 && fullDsi[realLength - 1] == 0)
            realLength--;

        realLength = Math.Max(2, realLength);
        _decoderConfig = realLength < dsiLength ? fullDsi[..realLength] : fullDsi;

        ParseAudioSpecificConfig(_decoderConfig);
    }

    /// <summary>
    /// Parses the AAC Audio Specific Config and updates sample rate / channels.
    /// </summary>
    /// <param name="asc">Raw bytes of the Audio Specific Config.</param>
    private void ParseAudioSpecificConfig(byte[] asc)
    {
        if (asc.Length < 2)
            return;

        int audioObjectType = (asc[0] >> 3) & 0x1F;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | ((asc[1] >> 7) & 0x01);
        int channelConfig = (asc[1] >> 3) & 0x0F;

        int baseSampleRate = AudioConstants.GetAacSampleRate(samplingFrequencyIndex);
        int baseChannels = AudioConstants.GetAacChannels(channelConfig);

        if (audioObjectType is 5 or 29)
        {
            int extRate = 0;
            if (asc.Length >= 3 && samplingFrequencyIndex != 15)
            {
                int extFreqIdx = ((asc[1] & 0x07) << 1) | ((asc[2] >> 7) & 0x01);
                if (extFreqIdx < 13)
                    extRate = AudioConstants.GetAacSampleRate(extFreqIdx);
            }

            _sampleRate = extRate > 0 ? extRate : (baseSampleRate > 0 ? baseSampleRate * 2 : _sampleRate);
            _channels = audioObjectType == 29 ? Math.Max(2, baseChannels) : baseChannels;
            return;
        }

        if (audioObjectType == 2 && asc.Length >= 5 && samplingFrequencyIndex != 15)
        {
            int syncExt = ((asc[2] & 0x1F) << 6) | ((asc[3] >> 2) & 0x3F);
            if (syncExt == 0x2B7)
            {
                int extAot = ((asc[3] & 0x03) << 3) | ((asc[4] >> 5) & 0x07);
                if (extAot == 5 && ((asc[4] >> 4) & 0x01) == 1)
                {
                    int extFreqIdx = asc[4] & 0x0F;
                    int extRate = extFreqIdx < 13 ? AudioConstants.GetAacSampleRate(extFreqIdx) : 0;
                    _sampleRate = extRate > 0 ? extRate : (baseSampleRate > 0 ? baseSampleRate * 2 : _sampleRate);

                    if (asc.Length >= 7)
                    {
                        int psSync = (asc[5] << 3) | ((asc[6] >> 5) & 0x07);
                        _channels = psSync == 0x548 && ((asc[6] >> 4) & 0x01) == 1
                            ? Math.Max(2, baseChannels)
                            : baseChannels;
                        return;
                    }

                    _channels = baseChannels;
                    return;
                }
            }
        }

        if (baseSampleRate > 0)
            _sampleRate = baseSampleRate;

        _channels = baseChannels;
    }

    /// <summary>
    /// Parses the <c>stsz</c> box.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of sample sizes.</returns>
    private async Task<List<uint>> ParseStszAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint defaultSize = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        uint count = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<uint>((int)count);

        if (defaultSize != 0)
        {
            for (uint i = 0; i < count; i++)
                result.Add(defaultSize);

            return result;
        }

        for (uint i = 0; i < count; i++)
            result.Add(await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false));

        return result;
    }

    /// <summary>
    /// Parses the <c>stsc</c> box.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of sample-to-chunk entries.</returns>
    private async Task<List<(uint firstChunk, uint samplesPerChunk, uint descIdx)>> ParseStscAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint entryCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<(uint firstChunk, uint samplesPerChunk, uint descIdx)>((int)entryCount);
        for (uint i = 0; i < entryCount; i++)
        {
            uint firstChunk = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint samplesPerChunk = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint descIdx = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            result.Add((firstChunk, samplesPerChunk, descIdx));
        }

        return result;
    }

    /// <summary>
    /// Parses the <c>stco</c> or <c>co64</c> box.
    /// </summary>
    /// <param name="use64BitOffsets">
    /// <c>true</c> for <c>co64</c>; <c>false</c> for <c>stco</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of absolute chunk offsets.</returns>
    private async Task<List<long>> ParseStcoAsync(bool use64BitOffsets, CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint count = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<long>((int)count);
        for (uint i = 0; i < count; i++)
        {
            long value = use64BitOffsets
                ? (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false)
                : await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// Parses the <c>stts</c> box.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of (count, delta) pairs for time-to-sample.</returns>
    private async Task<List<(uint count, uint delta)>> ParseSttsAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint count = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<(uint count, uint delta)>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint sampleCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint sampleDelta = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            result.Add((sampleCount, sampleDelta));
        }

        return result;
    }

    /// <summary>
    /// Builds a sample table for standard MP4 without an intermediate list of timestamps.
    /// </summary>
    /// <param name="sizes">Sample sizes.</param>
    /// <param name="stsc">Sample-to-chunk mapping rules.</param>
    /// <param name="offsets">Chunk offsets.</param>
    /// <param name="stts">Time-to-sample table.</param>
    /// <param name="timeScale">Track time scale.</param>
    private void BuildSampleTable(
        List<uint> sizes,
        List<(uint firstChunk, uint samplesPerChunk, uint descIdx)> stsc,
        List<long> offsets,
        List<(uint count, uint delta)> stts,
        uint timeScale)
    {
        if (sizes.Count == 0 || offsets.Count == 0)
            return;

        _samples = new List<SampleInfo>(sizes.Count);

        long currentTime = 0;
        int sttsIndex = 0;
        uint sttsConsumed = 0;
        int sampleIndex = 0;
        int stscIndex = 0;

        for (int chunkIndex = 0; chunkIndex < offsets.Count && sampleIndex < sizes.Count; chunkIndex++)
        {
            while (stscIndex + 1 < stsc.Count && stsc[stscIndex + 1].firstChunk - 1 <= chunkIndex)
                stscIndex++;

            uint samplesInChunk = stsc.Count > 0 ? stsc[stscIndex].samplesPerChunk : 1;
            long offset = offsets[chunkIndex];

            for (uint i = 0; i < samplesInChunk && sampleIndex < sizes.Count; i++)
            {
                uint delta = 20;
                if (sttsIndex < stts.Count)
                {
                    delta = stts[sttsIndex].delta;
                    sttsConsumed++;

                    if (sttsConsumed >= stts[sttsIndex].count)
                    {
                        sttsIndex++;
                        sttsConsumed = 0;
                    }
                }

                long timestampMs = timeScale > 0 ? currentTime * 1000 / timeScale : sampleIndex * 20L;
                int durationMs = timeScale > 0 ? (int)(delta * 1000 / timeScale) : 20;

                _samples.Add(new SampleInfo(offset, (int)sizes[sampleIndex], timestampMs, durationMs, true));

                offset += sizes[sampleIndex];
                currentTime += delta;
                sampleIndex++;
            }
        }

        if (_samples.Count > 0)
            _durationMs = _samples[^1].TimestampMs + _samples[^1].DurationMs;
    }

    /// <summary>
    /// Finds the sample index for seeking by timestamp.
    /// </summary>
    /// <param name="targetMs">Target position in milliseconds.</param>
    /// <returns>Index of the nearest preceding sample.</returns>
    private int BinarySearchSample(long targetMs)
    {
        int left = 0;
        int right = _samples.Count - 1;

        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            if (_samples[mid].TimestampMs <= targetMs)
                left = mid;
            else
                right = mid - 1;
        }

        return left;
    }
}