using System.Runtime.InteropServices;
using SharpJaad.AAC;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// AAC decoder based on SharpJaad with HE-AAC (SBR) support.
/// Optimized with SIMD conversion and buffer pooling.
/// </summary>
internal sealed class AacDecoder : IAudioDecoder
{
    private const int DefaultSampleRate = 44100;
    private const int DefaultChannels = 2;
    private const int DefaultMaxFrameSize = 1024;
    private const int MaxConsecutiveSilentErrors = 15;

    private Decoder? _decoder;
    private DecoderConfig? _config;
    private SampleBuffer? _sampleBuffer;
    private byte[]? _originalAsc;

    private readonly int _requestedSampleRate;
    private readonly int _requestedChannels;
    private bool _initialized;
    private bool _disposed;

    private bool _isHeAac;
    private int _outputSampleRate;
    private int _outputChannels;
    private int _consecutiveErrors;

    public AacDecoder(int sampleRate = DefaultSampleRate, int channels = DefaultChannels)
    {
        _requestedSampleRate = sampleRate;
        _requestedChannels = channels;

        Log.Debug($"[AacDecoder] Created: requested {sampleRate}Hz, {channels}ch");
    }

    public int SampleRate => _outputSampleRate > 0 ? _outputSampleRate :
                             _config?.GetSampleFrequency().GetFrequency() ?? _requestedSampleRate;
    public int Channels => _outputChannels > 0
        ? _outputChannels
        : (int?)_config?.GetChannelConfiguration() ?? _requestedChannels;
    public int MaxFrameSize
    {
        get
        {
            int baseLen = _config?.GetFrameLength() ?? DefaultMaxFrameSize;
            return (_isHeAac || (_outputSampleRate > 0 && _outputSampleRate <= 24000))
                ? baseLen * 2
                : baseLen;
        }
    }
    public AudioCodec Codec => AudioCodec.Aac;
    public bool IsInitialized => _initialized;

    public void Initialize(byte[] decoderSpecificInfo)
    {
        if (_initialized) return;

        if (decoderSpecificInfo == null || decoderSpecificInfo.Length < 2)
            throw new ArgumentException("Invalid decoder specific info", nameof(decoderSpecificInfo));

        try
        {
            _originalAsc = (byte[])decoderSpecificInfo.Clone();
            var ascInfo = ParseAudioSpecificConfig(decoderSpecificInfo);

            Log.Debug($"[AacDecoder] ASC analysis: objectType={ascInfo.ObjectType}, " +
                     $"baseRate={ascInfo.BaseSampleRate}, outputRate={ascInfo.OutputSampleRate}, " +
                     $"baseCh={ascInfo.BaseChannels}, outCh={ascInfo.OutputChannels}, isHE-AAC={ascInfo.IsHeAac}");

            if (ascInfo.IsHeAac)
                InitializeHeAac(ascInfo);
            else
                InitializeStandard(decoderSpecificInfo);

            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);
            _initialized = true;
        }
        catch (Exception ex)
        {
            throw new AudioDecoderException($"Failed to initialize AAC decoder: {ex.Message}");
        }
    }

    private void InitializeHeAac(AscInfo ascInfo)
    {
        _isHeAac = true;
        _outputSampleRate = ascInfo.OutputSampleRate;
        _outputChannels = ascInfo.OutputChannels;

        byte[] modifiedAsc = CreateAacLcAsc(ascInfo);

        Log.Debug($"[AacDecoder] HE-AAC: using modified ASC for core decoder: " +
                 $"{BitConverter.ToString(modifiedAsc)}");

        _config = DecoderConfig.ParseMP4DecoderSpecificInfo(modifiedAsc);
        _config.SetSBRPresent(true);
        _decoder = new Decoder(_config);

        Log.Info($"[AacDecoder] HE-AAC initialized: core={ascInfo.BaseSampleRate}Hz, " +
                $"output={_outputSampleRate}Hz, baseCh={ascInfo.BaseChannels}, outCh={_outputChannels}");
    }

    private void InitializeStandard(byte[] decoderSpecificInfo)
    {
        _isHeAac = false;

        _config = DecoderConfig.ParseMP4DecoderSpecificInfo(decoderSpecificInfo);
        _decoder = new Decoder(_config);
        _outputSampleRate = _config.GetSampleFrequency().GetFrequency();
        _outputChannels = (int)_config.GetChannelConfiguration();

        Log.Debug($"[AacDecoder] AAC-LC initialized: profile={_config.GetProfile()}, " +
                 $"sampleRate={_config.GetSampleFrequency()}, channels={_config.GetChannelConfiguration()}");
    }

    /// <summary>
    /// Completely recreates the internal decoder from the original ASC.
    /// Called after seek or on unrecoverable errors.
    /// </summary>
    private void RecreateDecoder()
    {
        if (_originalAsc == null || _config == null) return;

        try
        {
            if (_isHeAac)
            {
                var ascInfo = ParseAudioSpecificConfig(_originalAsc);
                byte[] modifiedAsc = CreateAacLcAsc(ascInfo);
                _config = DecoderConfig.ParseMP4DecoderSpecificInfo(modifiedAsc);
                _config.SetSBRPresent(true);
            }
            else
            {
                _config = DecoderConfig.ParseMP4DecoderSpecificInfo(_originalAsc);
            }

            _decoder = new Decoder(_config);
            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);
            _consecutiveErrors = 0;

            Log.Debug("[AacDecoder] Decoder recreated successfully");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AacDecoder] Recreate failed: {ex.Message}");
        }
    }

    private static byte[] CreateAacLcAsc(AscInfo info)
    {
        int freqIndex = GetSampleFrequencyIndex(info.BaseSampleRate);
        int objectType = 2; // AAC-LC

        byte byte0 = (byte)((objectType << 3) | ((freqIndex >> 1) & 0x07));
        byte byte1 = (byte)(((freqIndex & 0x01) << 7) | ((info.BaseChannels & 0x0F) << 3));

        return [byte0, byte1];
    }

    private static AscInfo ParseAudioSpecificConfig(byte[] asc)
    {
        if (asc.Length < 2)
        {
            return new AscInfo
            {
                ObjectType = 2,
                BaseSampleRate = 44100,
                OutputSampleRate = 44100,
                BaseChannels = 2,
                OutputChannels = 2,
                IsHeAac = false
            };
        }

        int audioObjectType = (asc[0] >> 3) & 0x1F;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | ((asc[1] >> 7) & 0x01);
        int channelConfig = (asc[1] >> 3) & 0x0F;

        int baseSampleRate = AudioConstants.GetAacSampleRate(samplingFrequencyIndex);
        int baseChannels = AudioConstants.GetAacChannels(channelConfig);

        bool isHeAac = audioObjectType is 5 or 29;
        int outputSampleRate = baseSampleRate;
        int outputChannels = audioObjectType == 29 ? Math.Max(2, baseChannels) : baseChannels;

        if (isHeAac)
        {
            int extRate = 0;
            if (asc.Length >= 3 && samplingFrequencyIndex != 15)
            {
                int extFreqIdx = ((asc[1] & 0x07) << 1) | ((asc[2] >> 7) & 0x01);
                if (extFreqIdx < 13)
                    extRate = AudioConstants.GetAacSampleRate(extFreqIdx);
            }

            outputSampleRate = extRate > 0 ? extRate : (baseSampleRate > 0 ? baseSampleRate * 2 : 44100);
        }
        else if (audioObjectType == 2 && asc.Length >= 5 && samplingFrequencyIndex != 15)
        {
            int syncExt = ((asc[2] & 0x1F) << 6) | ((asc[3] >> 2) & 0x3F);
            if (syncExt == 0x2B7)
            {
                int extAot = ((asc[3] & 0x03) << 3) | ((asc[4] >> 5) & 0x07);
                if (extAot == 5 && ((asc[4] >> 4) & 0x01) == 1)
                {
                    isHeAac = true;
                    int extFreqIdx = asc[4] & 0x0F;
                    int extRate = extFreqIdx < 13 ? AudioConstants.GetAacSampleRate(extFreqIdx) : 0;
                    outputSampleRate = extRate > 0 ? extRate : (baseSampleRate > 0 ? baseSampleRate * 2 : 44100);

                    if (asc.Length >= 7)
                    {
                        int psSync = (asc[5] << 3) | ((asc[6] >> 5) & 0x07);
                        if (psSync == 0x548 && ((asc[6] >> 4) & 0x01) == 1)
                            outputChannels = Math.Max(2, baseChannels);
                    }
                }
            }
        }

        return new AscInfo
        {
            ObjectType = audioObjectType,
            BaseSampleRate = baseSampleRate,
            OutputSampleRate = outputSampleRate,
            BaseChannels = baseChannels,
            OutputChannels = outputChannels,
            IsHeAac = isHeAac
        };
    }

    private static int GetSampleFrequencyIndex(int sampleRate)
    {
        return sampleRate switch
        {
            96000 => 0,
            88200 => 1,
            64000 => 2,
            48000 => 3,
            44100 => 4,
            32000 => 5,
            24000 => 6,
            22050 => 7,
            16000 => 8,
            12000 => 9,
            11025 => 10,
            8000 => 11,
            7350 => 12,
            _ => 4
        };
    }

    private readonly record struct AscInfo
    {
        public int ObjectType { get; init; }
        public int BaseSampleRate { get; init; }
        public int OutputSampleRate { get; init; }
        public int BaseChannels { get; init; }
        public int OutputChannels { get; init; }
        public bool IsHeAac { get; init; }
    }

    private void Initialize(Profile profile, SampleFrequency sampleFrequency, int channels)
    {
        if (_initialized) return;

        try
        {
            _config = new DecoderConfig();
            _config.SetProfile(profile);
            _config.SetSampleFrequency(sampleFrequency);
            _config.SetChannelConfiguration((ChannelConfiguration)channels);

            _decoder = new Decoder(_config);
            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);

            _outputSampleRate = sampleFrequency.GetFrequency();
            _initialized = true;

            Log.Debug($"[AacDecoder] Initialized: profile={profile}, rate={sampleFrequency}, ch={channels}");
        }
        catch (Exception ex)
        {
            throw new AudioDecoderException($"Failed to initialize AAC decoder: {ex.Message}");
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        Initialize(Profile.AAC_LC, GetSampleFrequency(_requestedSampleRate), _requestedChannels);
    }

    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        int nominalFrameSize = (_isHeAac ? 2 : 1) * (_config?.GetFrameLength() ?? DefaultMaxFrameSize);

        if (encodedData.IsEmpty)
        {
            int totalSamples = nominalFrameSize * Channels;
            outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
            return nominalFrameSize;
        }

        try
        {
            int targetCh = Channels;
            int sampleCount = _decoder!.DecodeFrameToFloat(
                encodedData,
                outputBuffer,
                targetCh,
                out int decodedRate,
                out int decodedChannels);

            if (decodedRate > _outputSampleRate)
            {
                _outputSampleRate = decodedRate;
                _isHeAac = true;
            }

            if (decodedChannels > _outputChannels)
                _outputChannels = decodedChannels;

            _consecutiveErrors = 0;
            return sampleCount;
        }
        catch (Exception ex) when (ex is AACException or InvalidCastException or IndexOutOfRangeException)
        {
            _consecutiveErrors++;

            if (_consecutiveErrors <= MaxConsecutiveSilentErrors)
            {
                if (_consecutiveErrors <= 3)
                    Log.Debug($"[AacDecoder] Decode error (attempt {_consecutiveErrors}): {ex.Message}");

                // Soft-flush state on transient errors instead of full allocation-heavy recreation
                if (_consecutiveErrors % 5 == 0)
                    _decoder?.Flush();

                int totalSamples = nominalFrameSize * Channels;
                outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
                return nominalFrameSize;
            }

            // Last resort: complete recreation
            RecreateDecoder();

            Log.Warn($"[AacDecoder] Persistent decode error after {_consecutiveErrors} attempts: {ex.Message}");

            // Return silence instead of throwing an exception — do not crash playback
            int total = nominalFrameSize * Channels;
            outputBuffer[..Math.Min(total, outputBuffer.Length)].Clear();
            return nominalFrameSize;
        }
    }

    /// <summary>
    /// Lightweight reset of the internal decoder state without full recreation.
    /// </summary>
    /// <remarks>
    /// <para><b>Industry Practice:</b></para>
    /// <list type="bullet">
    ///   <item>FFmpeg: <c>avcodec_flush_buffers</c> — zeroing internal buffers
    ///     via <c>memset</c> on <c>ChannelElement</c> without recreating context.</item>
    ///   <item>VLC: flush resets error state, allowing execution to resume after seek.</item>
    ///   <item>ExoPlayer: <c>canKeepCodec</c> — codec reuse, flush on seek.</item>
    /// </list>
    ///
    /// <para><b>Previous approach (<see cref="RecreateDecoder"/>):</b>
    /// <c>ParseMP4DecoderSpecificInfo</c> + <c>new Decoder</c> + <c>new SampleBuffer</c>
    /// = ~2ms + 10 allocations. Called on EVERY skip-frame (5 times for AAC)
    /// = ~10ms + 50 allocations per seek.</para>
    ///
    /// <para><b>New approach:</b> <c>Decoder.Flush()</c> zeroes out SyntacticElements
    /// (prediction/LTP state) via <c>Array.Clear</c>. FilterBank overlap
    /// is evicted by skip-frames. Cost: ~0.1ms + 1 allocation.</para>
    /// </remarks>
    public void FlushState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _decoder == null) return;

        _decoder.Flush();
        _consecutiveErrors = 0;
    }

    /// <summary>
    /// Decodes a frame after flushing the internal state.
    /// Called ONCE after seek via the <c>_decoderResetNeeded</c> CAS flag.
    /// </summary>
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        FlushState();
        return Decode(encodedData, outputBuffer);
    }

    /// <summary>
    /// SIMD-optimized conversion of byte[] (little-endian PCM16) → float[].
    /// </summary>
    /// <remarks>
    /// <para>SharpJaad returns PCM data as byte[] containing int16 samples.
    /// Conversion is invoked every frame (~50 times/sec), hence optimized via SIMD.</para>
    /// </remarks>
    internal static void ConvertBytesToFloat(byte[] pcmBytes, Span<float> output, int sampleCount)
    {
        int count = Math.Min(sampleCount, output.Length);

        // Interpret byte[] as short[] without copying
        var shorts = MemoryMarshal.Cast<byte, short>(pcmBytes.AsSpan(0, count * sizeof(short)));

        // Use shared SIMD conversion from OpusDecoder
        OpusDecoder.ConvertShortToFloat(shorts[..count], output);
    }

    private static SampleFrequency GetSampleFrequency(int rate)
    {
        return rate switch
        {
            96000 => SampleFrequency.SAMPLE_FREQUENCY_96000,
            88200 => SampleFrequency.SAMPLE_FREQUENCY_88200,
            64000 => SampleFrequency.SAMPLE_FREQUENCY_64000,
            48000 => SampleFrequency.SAMPLE_FREQUENCY_48000,
            44100 => SampleFrequency.SAMPLE_FREQUENCY_44100,
            32000 => SampleFrequency.SAMPLE_FREQUENCY_32000,
            24000 => SampleFrequency.SAMPLE_FREQUENCY_24000,
            22050 => SampleFrequency.SAMPLE_FREQUENCY_22050,
            16000 => SampleFrequency.SAMPLE_FREQUENCY_16000,
            12000 => SampleFrequency.SAMPLE_FREQUENCY_12000,
            11025 => SampleFrequency.SAMPLE_FREQUENCY_11025,
            8000 => SampleFrequency.SAMPLE_FREQUENCY_8000,
            _ => SampleFrequency.SAMPLE_FREQUENCY_44100
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _decoder = null;
        _config = null;
        _sampleBuffer = null;
        _originalAsc = null;
    }
}