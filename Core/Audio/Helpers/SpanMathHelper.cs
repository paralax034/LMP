using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Статические SIMD-ускоренные операции над <see cref="Span{T}"/> PCM-сэмплов.
/// </summary>
/// <remarks>
/// Заменяет <c>System.Numerics.Tensors.TensorPrimitives</c> без внешней зависимости.
/// Векторизация реализована через <see cref="Vector{T}"/> (SSE / AVX / NEON),
/// который входит в shared framework начиная с .NET Core 1.0.
/// </remarks>
internal static class SpanMathHelper
{
    /// <summary>
    /// Умножает каждый элемент <paramref name="samples"/> на константный
    /// <paramref name="gain"/> in-place.
    /// </summary>
    /// <remarks>
    /// Hot path: обрабатывает вектор из <see cref="Vector{T}.Count"/> float за итерацию,
    /// хвост (&lt; Count) — скалярно.
    /// Прямая перезапись через <c>ref var vRef</c> исключает промежуточные копии.
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
    /// Вычисляет <c>1.0f / d</c> через аппаратную инструкцию <c>rcpss</c> с одной итерацией уточнения Ньютона–Рафсона
    /// (<c>r1 = r0 * (2 - d * r0)</c>), обеспечивая точность ~23.8 бит (~1 ULP IEEE-754 float) без генерации <c>divss</c>.
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