using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Static SIMD-accelerated operations on PCM sample <see cref="Span{T}"/>.
/// </summary>
/// <remarks>
/// Replaces <c>System.Numerics.Tensors.TensorPrimitives</c> without external dependencies.
/// Vectorization is implemented via <see cref="Vector{T}"/> (SSE / AVX / NEON),
/// which is part of the shared framework starting with .NET Core 1.0.
/// </remarks>
internal static class SpanMathHelper
{
    /// <summary>
    /// Multiplies each element of <paramref name="samples"/> by a constant
    /// <paramref name="gain"/> in-place.
    /// </summary>
    /// <remarks>
    /// Hot path: processes a vector of <see cref="Vector{T}.Count"/> floats per iteration,
    /// with the tail (&lt; Count) processed scalarly.
    /// Direct overwrite via <c>ref var vRef</c> eliminates intermediate copies.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyByConstant(Span<float> samples, float gain)
    {
        ref float ptr = ref MemoryMarshal.GetReference(samples);
        int len = samples.Length;
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(gain);
            int lastBlock = len - Vector<float>.Count;

            for (; i <= lastBlock; i += Vector<float>.Count)
            {
                ref var vRef = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref ptr, i));
                vRef *= vGain;
            }
        }

        for (; i < len; i++)
            Unsafe.Add(ref ptr, i) *= gain;
    }

    /// <summary>
    /// Computes <c>1.0f / d</c> via hardware instruction <c>rcpss</c> with one Newton–Raphson refinement step
    /// (<c>r1 = r0 * (2 - d * r0)</c>), providing ~23.8 bit accuracy (~1 ULP IEEE-754 float) without generating <c>divss</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastReciprocal(float d)
    {
        if (Sse.IsSupported)
        {
            Vector128<float> vd = Vector128.CreateScalarUnsafe(d);
            Vector128<float> r0 = Sse.ReciprocalScalar(vd);
            Vector128<float> two = Vector128.CreateScalarUnsafe(2.0f);
            Vector128<float> r1 = Sse.MultiplyScalar(r0, Sse.SubtractScalar(two, Sse.MultiplyScalar(vd, r0)));
            return Vector128.ToScalar(r1);
        }

        return 1.0f / d;
    }
}