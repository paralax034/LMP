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
    private int _lastPacketSamples;
    private float _headerGainLinear = 1.0f;
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

    public OpusDecoder(int sampleRate = 48000, int channels = 2, ReadOnlySpan<byte> codecPrivate = default)
    {
        SampleRate = sampleRate;
        Channels = channels;
        MaxFrameSize = sampleRate * MaxFrameDurationMs / 1000;
        _lastPacketSamples = Math.Clamp(sampleRate * DefaultFrameDurationMs / 1000, 120, MaxFrameSize);

        // RFC 7845 §5.1: заголовок OpusHead (19+ байт) содержит Output Gain (signed 16-bit LE Q7.8 dB на смещении 16)
        if (codecPrivate.Length >= 18
            && codecPrivate[0] == (byte)'O'
            && codecPrivate[1] == (byte)'p'
            && codecPrivate[2] == (byte)'u'
            && codecPrivate[3] == (byte)'s')
        {
            short gainQ78 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(codecPrivate.Slice(16, 2));
            if (gainQ78 != 0)
            {
                float gainDb = gainQ78 / 256.0f;
                _headerGainLinear = MathF.Pow(10.0f, gainDb / 20.0f);
            }
        }

        if (IsNativeSupported)
        {
            _nativeDecoder = NativeOpus.opus_decoder_create(sampleRate, channels, out int error);
            if (error != 0 || _nativeDecoder == IntPtr.Zero)
            {
                _nativeDecoder = IntPtr.Zero;
                _managedDecoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
                Log.Debug($"[OpusDecoder] Created (Managed Concentus fallback float32): {sampleRate}Hz, {channels}ch");
            }
            else
            {
                Log.Debug($"[OpusDecoder] Created (Native libopus Float32 Zero-Alloc): {sampleRate}Hz, {channels}ch");
            }
        }
        else
        {
            _managedDecoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
            Log.Debug($"[OpusDecoder] Created (Managed Concentus float32): {sampleRate}Hz, {channels}ch");
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

        int maxFrames = Math.Min(MaxFrameSize, outputBuffer.Length / Channels);
        if (maxFrames <= 0)
            return 0;

        if (_nativeDecoder != IntPtr.Zero)
        {
            int samples;
            fixed (byte* inPtr = &MemoryMarshal.GetReference(encodedData))
            fixed (float* outPtr = &MemoryMarshal.GetReference(outputBuffer))
            {
                // Нативный декод напрямую в IEEE 754 float32 (без 16-битного квантования и без hard-clipping'а пиков > 1.0f!)
                samples = NativeOpus.opus_decode_float(
                    _nativeDecoder,
                    inPtr,
                    encodedData.Length,
                    outPtr,
                    maxFrames,
                    0);
            }

            if (samples < 0)
            {
                Log.Warn($"[OpusDecoder] Native float decode error code: {samples}");
                throw new AudioDecoderException($"OPUS native decode failed with error code: {samples}");
            }

            if (samples > 0)
            {
                _lastPacketSamples = samples;
                if (MathF.Abs(_headerGainLinear - 1.0f) > 1e-4f)
                    Helpers.SpanMathHelper.MultiplyByConstant(outputBuffer[..(samples * Channels)], _headerGainLinear);
            }

            return samples;
        }

        try
        {
            int samples = _managedDecoder!.Decode(encodedData, outputBuffer, maxFrames, false);

            if (samples > 0)
            {
                _lastPacketSamples = samples;
                if (MathF.Abs(_headerGainLinear - 1.0f) > 1e-4f)
                    Helpers.SpanMathHelper.MultiplyByConstant(outputBuffer[..(samples * Channels)], _headerGainLinear);
            }

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
        int expectedSamples = Math.Clamp(_lastPacketSamples, 120, Math.Min(MaxFrameSize, outputBuffer.Length / Channels));
        if (expectedSamples <= 0)
            return 0;

        if (_nativeDecoder != IntPtr.Zero)
        {
            int samples;
            fixed (float* outPtr = &MemoryMarshal.GetReference(outputBuffer))
            {
                samples = NativeOpus.opus_decode_float(
                    _nativeDecoder,
                    null,
                    0,
                    outPtr,
                    expectedSamples,
                    0);
            }

            if (samples > 0)
            {
                if (MathF.Abs(_headerGainLinear - 1.0f) > 1e-4f)
                    Helpers.SpanMathHelper.MultiplyByConstant(outputBuffer[..(samples * Channels)], _headerGainLinear);
                return samples;
            }
        }

        int totalSamples = expectedSamples * Channels;
        outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
        return expectedSamples;
    }

    /// <summary>
    /// SIMD-оптимизированная конвертация short[] → float[].
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
        internal static unsafe partial int opus_decode_float(
            IntPtr st,
            byte* data,
            int len,
            float* pcm,
            int frame_size,
            int decode_fec);
    }
}