using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Concentus;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// OPUS декодер на базе нативной библиотеки <c>opus.dll</c> (libopus) с P/Invoke без аллокаций в куче,
/// и автоматическим фоллбэком на управляемый Concentus при отсутствии нативной библиотеки.
/// Оптимизирован с SIMD конвертацией short→float и гарантированным освобождением неуправляемой памяти.
/// </summary>
public sealed partial class OpusDecoder : IAudioDecoder
{
    private const int MaxFrameDurationMs = 120;
    private const int DefaultFrameDurationMs = 20;

    private static readonly bool IsNativeSupported = CheckNativeSupport();

    private IntPtr _nativeDecoder;
    private readonly IOpusDecoder? _managedDecoder;
    private readonly short[] _shortBuffer;
    private int _lastPacketSamples;
    private bool _disposed;

    /// <summary>
    /// Проверяет доступность нативной библиотеки <c>opus</c> в текущем окружении.
    /// </summary>
    private static bool CheckNativeSupport()
    {
        try
        {
            IntPtr dec = NativeOpus.opus_decoder_create(48000, 2, out int err);
            if (err == 0 && dec != IntPtr.Zero)
            {
                NativeOpus.opus_decoder_destroy(dec);
                return true;
            }
        }
        catch
        {
            // Нативная библиотека opus не найдена или несовместима с архитектурой
        }
        return false;
    }

    public OpusDecoder(int sampleRate = 48000, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;
        MaxFrameSize = sampleRate * MaxFrameDurationMs / 1000;
        _lastPacketSamples = Math.Clamp(sampleRate * DefaultFrameDurationMs / 1000, 120, MaxFrameSize);
        _shortBuffer = new short[MaxFrameSize * channels];

        if (IsNativeSupported)
        {
            _nativeDecoder = NativeOpus.opus_decoder_create(sampleRate, channels, out int error);
            if (error != 0 || _nativeDecoder == IntPtr.Zero)
            {
                _nativeDecoder = IntPtr.Zero;
                _managedDecoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
                Log.Debug($"[OpusDecoder] Created (Managed Concentus fallback): {sampleRate}Hz, {channels}ch");
            }
            else
            {
                Log.Debug($"[OpusDecoder] Created (Native libopus Zero-Alloc): {sampleRate}Hz, {channels}ch");
            }
        }
        else
        {
            _managedDecoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
            Log.Debug($"[OpusDecoder] Created (Managed Concentus): {sampleRate}Hz, {channels}ch");
        }
    }

    /// <summary>
    /// Финализатор гарантирует освобождение неуправляемого состояния <c>opus_decoder_destroy</c>,
    /// если вызывающий код не вызвал <see cref="Dispose"/>.
    /// </summary>
    ~OpusDecoder()
    {
        ReleaseNativeDecoder();
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int MaxFrameSize { get; }
    public AudioCodec Codec => AudioCodec.Opus;
    public bool IsInitialized => true;

    /// <summary>Признак использования нативного P/Invoke декодера без аллокаций в куче.</summary>
    internal bool IsUsingNative => _nativeDecoder != IntPtr.Zero;

    public unsafe int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (encodedData.IsEmpty)
            return DecodePLC(outputBuffer);

        if (_nativeDecoder != IntPtr.Zero)
        {
            Span<short> shortSpan = _shortBuffer.AsSpan(0, MaxFrameSize * Channels);
            int samples;

            fixed (byte* inPtr = &MemoryMarshal.GetReference(encodedData))
            fixed (short* outPtr = &MemoryMarshal.GetReference(shortSpan))
            {
                samples = NativeOpus.opus_decode(
                    _nativeDecoder,
                    inPtr,
                    encodedData.Length,
                    outPtr,
                    MaxFrameSize,
                    0);
            }

            if (samples < 0)
            {
                Log.Warn($"[OpusDecoder] Native decode error code: {samples}");
                throw new AudioDecoderException($"OPUS native decode failed with error code: {samples}");
            }

            if (samples > 0)
                _lastPacketSamples = samples;

            ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
            return samples;
        }

        try
        {
            Span<short> shortSpan = _shortBuffer.AsSpan(0, MaxFrameSize * Channels);
            int samples = _managedDecoder!.Decode(encodedData, shortSpan, MaxFrameSize);

            if (samples > 0)
                _lastPacketSamples = samples;

            ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
            return samples;
        }
        catch (Exception ex)
        {
            Log.Warn($"[OpusDecoder] Decode error: {ex.Message}");
            throw new AudioDecoderException($"OPUS decode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Декодирует фрейм после seek с seek-safe семантикой сброса состояния.
    /// </summary>
    /// <param name="encodedData">Сжатые данные одного Opus-фрейма.</param>
    /// <param name="outputBuffer">Выходной PCM float32 буфер.</param>
    /// <returns>Количество декодированных семплов на канал.</returns>
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        FlushState();
        return Decode(encodedData, outputBuffer);
    }

    /// <summary>
    /// Выполняет лёгкий flush внутреннего состояния Opus-декодера.
    /// </summary>
    public void FlushState()
    {
        if (_disposed) return;

        if (_nativeDecoder != IntPtr.Zero)
        {
            const int OPUS_RESET_STATE = 4028;
            _ = NativeOpus.opus_decoder_ctl(_nativeDecoder, OPUS_RESET_STATE);
        }
        else
        {
            _managedDecoder?.ResetState();
        }
    }

    private unsafe int DecodePLC(Span<float> outputBuffer)
    {
        int expectedSamples = Math.Clamp(_lastPacketSamples, 120, MaxFrameSize);

        // В нативном libopus вызов opus_decode с null-указателем пакета выполняет PLC за 0 аллокаций
        // ровно на размер последнего декодированного фрейма (expectedSamples).
        if (_nativeDecoder != IntPtr.Zero)
        {
            Span<short> shortSpan = _shortBuffer.AsSpan(0, expectedSamples * Channels);

            int samples;
            fixed (short* outPtr = &MemoryMarshal.GetReference(shortSpan))
            {
                samples = NativeOpus.opus_decode(
                    _nativeDecoder,
                    null,
                    0,
                    outPtr,
                    expectedSamples,
                    0);
            }

            if (samples > 0)
            {
                ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
                return samples;
            }
        }

        // Fallback: нулевой кадр тишины на длительность последнего пакета без аллокаций в куче.
        int totalSamples = expectedSamples * Channels;
        outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
        return expectedSamples;
    }

    /// <summary>
    /// SIMD-оптимизированная конвертация short[] → float[].
    /// На hot path (~50 вызовов/сек × 960 samples = ~48000 операций/сек).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ConvertShortToFloat(ReadOnlySpan<short> src, Span<float> dst)
    {
        const float scale = 1f / 32768f;
        int len = Math.Min(src.Length, dst.Length);
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= Vector<short>.Count)
        {
            var scaleVec = new Vector<float>(scale);
            var shorts = MemoryMarshal.Cast<short, Vector<short>>(src[..len]);
            int floatVecCount = Vector<float>.Count;

            for (int vi = 0; vi < shorts.Length; vi++)
            {
                Vector.Widen(shorts[vi], out var lo, out var hi);

                var floatLo = Vector.ConvertToSingle(lo) * scaleVec;
                var floatHi = Vector.ConvertToSingle(hi) * scaleVec;

                int baseIdx = vi * Vector<short>.Count;
                if (baseIdx + floatVecCount <= len)
                    floatLo.CopyTo(dst.Slice(baseIdx, floatVecCount));
                if (baseIdx + floatVecCount * 2 <= len)
                    floatHi.CopyTo(dst.Slice(baseIdx + floatVecCount, floatVecCount));
            }

            i = shorts.Length * Vector<short>.Count;
        }

        for (; i < len; i++)
        {
            dst[i] = src[i] * scale;
        }
    }

    private void ReleaseNativeDecoder()
    {
        IntPtr handle = Interlocked.Exchange(ref _nativeDecoder, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeOpus.opus_decoder_destroy(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseNativeDecoder();
        GC.SuppressFinalize(this);

        if (_managedDecoder != null)
        {
            _managedDecoder.ResetState();
            if (_managedDecoder is IDisposable d)
                d.Dispose();
        }
    }

    /// <summary>
    /// P/Invoke привязки к нативной библиотеке <c>opus</c> (из пакета Concentus.Native / системной libopus).
    /// </summary>
    private static partial class NativeOpus
    {
        private const string LibraryName = "opus";

        [LibraryImport(LibraryName)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial IntPtr opus_decoder_create(int Fs, int channels, out int error);

        [LibraryImport(LibraryName)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void opus_decoder_destroy(IntPtr st);

        [LibraryImport(LibraryName)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int opus_decoder_ctl(IntPtr st, int request);

        [LibraryImport(LibraryName)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int opus_decode(
            IntPtr st,
            byte* data,
            int len,
            short* pcm,
            int frame_size,
            int decode_fec);
    }
}