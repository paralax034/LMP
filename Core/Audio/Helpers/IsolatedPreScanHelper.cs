using System.Buffers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Static helper for EBU R128 pre-scan via a fully isolated pipeline.
/// </summary>
/// <remarks>
/// <para>Creates and disposes its own <see cref="FileStream"/>,
/// <see cref="IContainerParser"/>, and <see cref="IAudioDecoder"/>.
/// Shared resources of the calling <c>AudioPipeline</c> remain untouched.</para>
/// <para>All decode/filter buffers are rented from <see cref="ArrayPool{T}.Shared"/>
/// and returned in <c>finally</c>.</para>
/// </remarks>
internal static class IsolatedPreScanHelper
{
    #region Constants

    /// <summary>Decimation factor for Opus — Concentus.Native is fast enough at 5.</summary>
    private const int DecimationFactorOpus = 5;

    /// <summary>Decimation factor for AAC — SharpJaad is slower, so we use 8.</summary>
    private const int DecimationFactorAac = 8;

    /// <summary>Opus @ 48kHz: 20ms frame = 960 samples.</summary>
    private const int NominalSamplesOpus = 960;

    /// <summary>AAC @ 44100Hz: ~23ms frame = 1024 samples.</summary>
    private const int NominalSamplesAac = 1024;

    private const double LufsOffset = -0.691;
    private const double AbsoluteGateThresholdLufs = -70.0;
    private const double GatingBlockSeconds = 0.4;

    #endregion

    /// <summary>
    /// Performs an EBU R128 pre-scan for the specified audio file.
    /// </summary>
    /// <param name="filePath">Path to the audio file (local or cache).</param>
    /// <param name="codec">File codec (Opus / AAC).</param>
    /// <param name="targetLufs">Target normalization LUFS level.</param>
    /// <param name="maxGain">Maximum allowed gain.</param>
    /// <param name="scanMaxSeconds">Maximum scan duration in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A pair of <c>(IntegratedLufs, RawGain)</c>.
    /// <c>RawGain = 1.0f</c> if the scan produced no result.
    /// <c>IntegratedLufs = <see cref="float.NaN"/></c> on format error.
    /// </returns>
    public static async Task<(float IntegratedLufs, float RawGain)> RunAsync(
        string filePath,
        AudioCodec codec,
        float targetLufs,
        float maxGain,
        float scanMaxSeconds,
        CancellationToken ct)
    {
        FileStream? fs = null;
        IContainerParser? parser = null;
        IAudioDecoder? decoder = null;
        float[]? decodeBuffer = null;
        double[]? filteredBuffer = null;

        try
        {
            fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: AudioConstants.CacheFileBufferSize,
                useAsync: false);

            var format = await DetectFormatAsync(fs, ct).ConfigureAwait(false);
            if (format == AudioFormat.Unknown)
                return (float.NaN, 1.0f);

            parser = CreateParser(format, fs);
            if (!await parser.ParseHeadersAsync(ct).ConfigureAwait(false))
                return (float.NaN, 1.0f);

            int sampleRate = parser.SampleRate > 0
                ? parser.SampleRate
                : AudioConstants.DefaultSampleRate;
            int channels = parser.Channels > 0
                ? parser.Channels
                : AudioConstants.DefaultChannels;

            decoder = CreateDecoder(codec, parser, sampleRate, channels);

            int maxFrames = AudioConstants.DecoderBufferFrames * channels;
            decodeBuffer = ArrayPool<float>.Shared.Rent(maxFrames);
            filteredBuffer = ArrayPool<double>.Shared.Rent(maxFrames);

            int decimationFactor = codec == AudioCodec.Aac ? DecimationFactorAac : DecimationFactorOpus;
            int nominalSamples = codec == AudioCodec.Aac ? NominalSamplesAac : NominalSamplesOpus;

            return await ScanFramesAsync(
                parser, decoder,
                sampleRate, channels,
                decodeBuffer, filteredBuffer,
                targetLufs, maxGain,
                decimationFactor, nominalSamples,
                scanMaxSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            decoder?.Dispose();
            if (parser != null) await parser.DisposeAsync().ConfigureAwait(false);
            if (fs != null) await fs.DisposeAsync().ConfigureAwait(false);
            if (decodeBuffer != null) ArrayPool<float>.Shared.Return(decodeBuffer);
            if (filteredBuffer != null) ArrayPool<double>.Shared.Return(filteredBuffer);
        }
    }

    #region Private Helpers

    /// <summary>Detects format by magic bytes and resets stream position.</summary>
    private static async Task<AudioFormat> DetectFormatAsync(FileStream fs, CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(AudioConstants.FormatDetectionHeaderSize);
        try
        {
            int totalRead = 0;
            while (totalRead < AudioConstants.FormatDetectionHeaderSize)
            {
                int read = await fs.ReadAsync(
                    header.AsMemory(totalRead, AudioConstants.FormatDetectionHeaderSize - totalRead),
                    ct).ConfigureAwait(false);

                if (read == 0) break;
                totalRead += read;
            }
            fs.Position = 0;
            return AudioSourceFactory.DetectFormatByMagic(header.AsSpan(0, totalRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>Creates an isolated parser for the specified format.</summary>
    /// <exception cref="NotSupportedException">Unsupported format.</exception>
    private static IContainerParser CreateParser(AudioFormat format, FileStream fs) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new Parsers.WebMContainerParser(fs),
        AudioFormat.Mp4 => new Parsers.Mp4ContainerParser(fs),
        _ => throw new NotSupportedException(
            $"[IsolatedPreScanHelper] Unsupported format: {format}")
    };

    /// <summary>Creates an isolated decoder handling codec-specific initialization.</summary>
    /// <exception cref="NotSupportedException">Unsupported codec.</exception>
    private static IAudioDecoder CreateDecoder(
        AudioCodec codec, IContainerParser parser, int sampleRate, int channels) => codec switch
        {
            AudioCodec.Opus => new Decoders.OpusDecoder(sampleRate, channels),
            AudioCodec.Aac => CreateAacDecoder(parser, sampleRate, channels),
            _ => throw new NotSupportedException(
                $"[IsolatedPreScanHelper] Unsupported codec: {codec}")
        };

    private static Decoders.AacDecoder CreateAacDecoder(
        IContainerParser parser, int sampleRate, int channels)
    {
        var dec = new Decoders.AacDecoder(sampleRate, channels);
        if (parser.DecoderConfig != null)
            dec.Initialize(parser.DecoderConfig);
        return dec;
    }

    /// <summary>
    /// Main loop for reading frames and calculating EBU R128 integrated LUFS.
    /// </summary>
    private static async Task<(float IntegratedLufs, float RawGain)> ScanFramesAsync(
        IContainerParser parser,
        IAudioDecoder decoder,
        int sampleRate,
        int channels,
        float[] decodeBuffer,
        double[] filteredBuffer,
        float targetLufs,
        float maxGain,
        int decimationFactor,
        int nominalSamplesPerFrame,
        float scanMaxSeconds,
        CancellationToken ct)
    {
        var scanFilter = new KWeightingFilter(sampleRate, channels);
        var blockSumSq = new double[channels];
        var blockPowers = new double[EbuR128Analyzer.MaxScanGatingBlocks];

        int blockCount = 0;
        int blockFrameCount = 0;
        long totalFrames = 0;
        long maxFrames = (long)(sampleRate * scanMaxSeconds);
        int gatingBlockSize = (int)(sampleRate * GatingBlockSeconds);
        int frameIndex = 0;

        while (!ct.IsCancellationRequested && totalFrames < maxFrames)
        {
            var frame = await parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
            if (frame == null) break;

            totalFrames += nominalSamplesPerFrame;
            frameIndex++;

            if (frameIndex % decimationFactor != 0)
                continue;

            scanFilter.Reset();

            int decoded = decoder.Decode(frame.Value.Data.Span, decodeBuffer);
            if (decoded <= 0) continue;

            int samplesToProcess = decoded * channels;

            scanFilter.ProcessBlock(
                decodeBuffer.AsSpan(0, samplesToProcess),
                filteredBuffer.AsSpan(0, samplesToProcess));

            ref double filteredRef = ref System.Runtime.InteropServices.MemoryMarshal
                .GetArrayDataReference(filteredBuffer);
            ref double sumSqRef = ref System.Runtime.InteropServices.MemoryMarshal
                .GetArrayDataReference(blockSumSq);

            for (int f = 0; f < decoded; f++)
            {
                int offset = f * channels;
                for (int ch = 0; ch < channels; ch++)
                {
                    double val = System.Runtime.CompilerServices.Unsafe
                        .Add(ref filteredRef, offset + ch);
                    System.Runtime.CompilerServices.Unsafe.Add(ref sumSqRef, ch) += val * val;
                }

                if (++blockFrameCount >= gatingBlockSize)
                {
                    double channelPowerSum = 0.0;
                    for (int ch = 0; ch < channels; ch++)
                        channelPowerSum += System.Runtime.CompilerServices.Unsafe
                            .Add(ref sumSqRef, ch) / blockFrameCount;

                    double blockLufs = LufsOffset + 10.0 * Math.Log10(
                        Math.Max(channelPowerSum, 1e-20));

                    if (blockLufs > AbsoluteGateThresholdLufs
                        && blockCount < EbuR128Analyzer.MaxScanGatingBlocks)
                    {
                        blockPowers[blockCount++] = channelPowerSum;
                    }

                    Array.Clear(blockSumSq, 0, channels);
                    blockFrameCount = 0;
                }
            }
        }

        float integratedLufs = EbuR128Analyzer.ComputeIntegratedLufsFromBlocks(
            blockPowers, blockCount);
        float rawGain = EbuR128Analyzer.ComputeGainFromLufs(
            integratedLufs, targetLufs, maxGain);

        return (integratedLufs, rawGain);
    }

    #endregion
}