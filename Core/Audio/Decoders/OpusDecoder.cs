using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Concentus;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// OPUS decoder based on the native <c>opus.dll</c> library (libopus) using allocation-free P/Invoke,
/// featuring automatic fallback to managed Concentus when the native library is missing.
/// Optimized with SIMD short→float conversion and guaranteed unmanaged memory release.
/// </summary>
internal sealed partial class OpusDecoder : IAudioDecoder
{
    private const int MaxFrameDurationMs = 120;
    private const int DefaultFrameDurationMs = 20;

    private static readonly bool IsNativeSupported = CheckNativeSupport();

    private IntPtr _nativeDecoder;
    private int _nativeRefCount;
    private int _nativeDisposeSignaled;
    private readonly IOpusDecoder? _managedDecoder;
    private int _lastPacketSamples;
    private float _headerGainLinear = 1.0f;
    private volatile bool _disposed;

    /// <summary>
    /// Checks the availability of the native <c>opus</c> library in the current environment.
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
            // Native opus library was not found or is incompatible with the target architecture
        }
        return false;
    }

    public OpusDecoder(int sampleRate = 48000, int channels = 2, ReadOnlySpan<byte> codecPrivate = default)
    {
        SampleRate = sampleRate;
        Channels = channels;
        MaxFrameSize = sampleRate * MaxFrameDurationMs / 1000;
        _lastPacketSamples = Math.Clamp(sampleRate * DefaultFrameDurationMs / 1000, 120, MaxFrameSize);

        // RFC 7845 §5.1: OpusHead header (19+ bytes) contains Output Gain (signed 16-bit LE Q7.8 dB at offset 16)
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
                Volatile.Write(ref _nativeRefCount, 1);
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
    /// Finalizer guarantees the release of unmanaged <c>opus_decoder_destroy</c> state
    /// if the caller failed to invoke <see cref="Dispose"/>.
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

    /// <summary>Indicates whether the zero-allocation native P/Invoke decoder is in use.</summary>
    internal bool IsUsingNative => _nativeDecoder != IntPtr.Zero;

    public unsafe int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (encodedData.IsEmpty)
            return DecodePLC(outputBuffer);

        int maxFrames = Math.Min(MaxFrameSize, outputBuffer.Length / Channels);
        if (maxFrames <= 0)
            return 0;

        if (TryAcquireNativeDecoder(out IntPtr nativeDec))
        {
            int samples;
            try
            {
                fixed (byte* inPtr = &MemoryMarshal.GetReference(encodedData))
                fixed (float* outPtr = &MemoryMarshal.GetReference(outputBuffer))
                {
                    // Native decode directly to IEEE 754 float32 (without 16-bit quantization and without hard-clipping peaks > 1.0f!)
                    samples = NativeOpus.opus_decode_float(
                        nativeDec,
                        inPtr,
                        encodedData.Length,
                        outPtr,
                        maxFrames,
                        0);
                }
            }
            finally
            {
                ReleaseNativeDecoderRef();
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

        ObjectDisposedException.ThrowIf(_disposed || _managedDecoder == null, this);

        try
        {
            int samples = _managedDecoder.Decode(encodedData, outputBuffer, maxFrames, false);

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
    /// Decodes a frame after seeking with seek-safe state reset semantics.
    /// </summary>
    /// <param name="encodedData">Compressed data of a single Opus frame.</param>
    /// <param name="outputBuffer">Output PCM float32 buffer.</param>
    /// <returns>Number of decoded samples per channel.</returns>
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        FlushState();
        return Decode(encodedData, outputBuffer);
    }

    /// <summary>
    /// Performs a lightweight flush of the internal Opus decoder state.
    /// </summary>
    public void FlushState()
    {
        if (_disposed) return;

        if (TryAcquireNativeDecoder(out IntPtr nativeDec))
        {
            try
            {
                const int OPUS_RESET_STATE = 4028;
                _ = NativeOpus.opus_decoder_ctl(nativeDec, OPUS_RESET_STATE);
            }
            finally
            {
                ReleaseNativeDecoderRef();
            }
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

        if (TryAcquireNativeDecoder(out IntPtr nativeDec))
        {
            int samples;
            try
            {
                fixed (float* outPtr = &MemoryMarshal.GetReference(outputBuffer))
                {
                    samples = NativeOpus.opus_decode_float(
                        nativeDec,
                        null,
                        0,
                        outPtr,
                        expectedSamples,
                        0);
                }
            }
            finally
            {
                ReleaseNativeDecoderRef();
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
    /// SIMD-optimized short[] → float[] conversion.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireNativeDecoder(out IntPtr handle)
    {
        while (true)
        {
            int count = Volatile.Read(ref _nativeRefCount);
            if (count <= 0)
            {
                handle = IntPtr.Zero;
                return false;
            }

            if (Interlocked.CompareExchange(ref _nativeRefCount, count + 1, count) == count)
            {
                handle = Volatile.Read(ref _nativeDecoder);
                if (handle != IntPtr.Zero)
                    return true;

                ReleaseNativeDecoderRef();
                handle = IntPtr.Zero;
                return false;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseNativeDecoderRef()
    {
        if (Interlocked.Decrement(ref _nativeRefCount) == 0)
        {
            IntPtr handle = Interlocked.Exchange(ref _nativeDecoder, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                NativeOpus.opus_decoder_destroy(handle);
        }
    }

    private void ReleaseNativeDecoder()
    {
        if (Interlocked.Exchange(ref _nativeDisposeSignaled, 1) == 0 && _nativeDecoder != IntPtr.Zero)
            ReleaseNativeDecoderRef();
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
    /// P/Invoke bindings for the native <c>opus</c> library (from the Concentus.Native package / system libopus).
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