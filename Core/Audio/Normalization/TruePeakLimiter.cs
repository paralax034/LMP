using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Normalization;

public sealed class TruePeakLimiter
{
    #region Constants

    /// <summary>
    /// Потолок True Peak защиты (-0.13 dBFS = 0.985f).
    /// Предотвращает срабатывание системного лимитера Windows (CAudioLimiter APO в audiodg.exe) на 95-100% громкости.
    /// </summary>
    private const float Ceiling = 0.985f;

    /// <summary>
    /// Порог начала мягкого асимптотического скругления пиков (0.88f) на первых миллисекундах атаки.
    /// Гарантирует 0 плоских срезов (FlatClipped == 0) и отсутствие пробоя Ceiling (0.985f) без скачков огибающей.
    /// </summary>
    private const float KneeStart = 0.88f;
    private const float KneeSpan = Ceiling - KneeStart; // 0.105f
    private const float InvKneeSpan = 1.0f / KneeSpan;

    /// <summary>Время плавной атаки огибающей (4 мс) — полностью исключает щелчки и модуляцию ("дрожащую нитку") на транзиентах.</summary>
    private const float AttackMs = 4.0f;

    private const float PeakHoldMs = 30f;

    /// <summary>Release time в миллисекундах (плавный возврат после завершения басовой партии).</summary>
    private const float ReleaseMs = 400f;

    #endregion

    #region State

    private float _envelopeGain;
    private float _targetEnvelope;
    private int _holdFramesRemaining;

    /// <summary>
    /// Возвращает текущее состояние ослабления огибающей лимитера.
    /// Значение 1.0f означает, что лимитер полностью восстановился и не ослабляет сигнал.
    /// </summary>
    public float EnvelopeGain => _envelopeGain;

    private readonly int _holdFrames;
    private readonly float _attackCoef;
    private readonly float _releaseCoef;

    #endregion

    /// <summary>
    /// Создаёт True Peak Limiter с указанным sample rate.
    /// </summary>
    /// <param name="sampleRate">Sample rate аудио (Гц).</param>
    public TruePeakLimiter(int sampleRate)
    {
        int rate = Math.Max(8000, sampleRate);
        _envelopeGain = 1.0f;
        _targetEnvelope = 1.0f;
        _holdFramesRemaining = 0;

        _holdFrames = Math.Max(1, (int)(rate * PeakHoldMs / 1000f));
        float attackSamples = rate * AttackMs / 1000f;
        float releaseSamples = rate * ReleaseMs / 1000f;

        _attackCoef = MathF.Exp(-1f / attackSamples);
        _releaseCoef = MathF.Exp(-1f / releaseSamples);
    }

    /// <summary>
    /// Гладкий C2-непрерывный асимптотический сатуратор для положительной амплитуды пика.
    /// В точке <see cref="KneeStart"/> имеет <c>f'(0) = 1</c> и <c>f''(0) = 0</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftCeilingPositive(float abs)
    {
        float u = (abs - KneeStart) * InvKneeSpan;
        float u2 = u * u;
        float sum = u + u2;
        return KneeStart + KneeSpan * (sum * Helpers.SpanMathHelper.FastReciprocal(1.0f + sum));
    }

    /// <summary>
    /// Множитель, который мягко вписывает пик кадра под <see cref="Ceiling"/> той же C2-кривой, что и сам лимитер.
    /// При пике не выше <see cref="KneeStart"/> возвращает ровно 1: сигнал, не дошедший до колена, проходит бит-в-бит.
    /// Нужен узлам после лимитера, которые сами порождают межотсчётные пики (ресемплер WASAPI).
    /// </summary>
    /// <param name="peak">Модуль наибольшего отсчёта кадра по всем каналам.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float SoftCeilingScale(float peak)
        => peak > KneeStart ? SoftCeilingPositive(peak) * Helpers.SpanMathHelper.FastReciprocal(peak) : 1.0f;

    /// <summary>
    /// Обрабатывает interleaved PCM буфер in-place (Zero-Latency, 100% сохранение стерео-баланса L/R и фазы при Seek).
    /// </summary>
    /// <param name="samples">Interleaved PCM сэмплы (in-place обработка).</param>
    /// <param name="crossfader">Ссылка на crossfader для per-sample gain interpolation.</param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> samples, ref GainCrossfader crossfader)
    {
        int length = samples.Length;
        if (length == 0) return;

        ref float samplesRef = ref MemoryMarshal.GetReference(samples);

        float envGain = _envelopeGain;
        float targetEnv = _targetEnvelope;
        int holdRem = _holdFramesRemaining;
        int holdMax = _holdFrames;
        float attackCoef = _attackCoef;
        float releaseCoef = _releaseCoef;

        int i = 0;
        int pairLimit = length & ~1;

        for (; i < pairLimit; i += 2)
        {
            float gain = crossfader.AdvanceFrame(2);
            float inL = Unsafe.Add(ref samplesRef, i) * gain;
            float inR = Unsafe.Add(ref samplesRef, i + 1) * gain;

            float absL = MathF.Abs(inL);
            float absR = MathF.Abs(inR);
            float absPeak = absL > absR ? absL : absR;
            float neededGain = absPeak > KneeStart
                ? KneeStart * Helpers.SpanMathHelper.FastReciprocal(absPeak)
                : 1.0f;

            if (neededGain <= targetEnv)
            {
                targetEnv = neededGain;
                holdRem = holdMax;
            }
            else if (neededGain <= targetEnv * 1.06f && neededGain < 1.0f)
            {
                holdRem = holdMax;
            }
            else if (holdRem > 0)
            {
                holdRem--;
            }
            else
            {
                targetEnv = 1.0f + releaseCoef * (targetEnv - 1.0f);
            }

            float coef = targetEnv < envGain ? attackCoef : releaseCoef;
            envGain = targetEnv + coef * (envGain - targetEnv);

            float outL = inL * envGain;
            float outR = inR * envGain;
            float outPeak = absPeak * envGain;
            if (outPeak > KneeStart)
            {
                float softScale = SoftCeilingPositive(outPeak) * Helpers.SpanMathHelper.FastReciprocal(outPeak);
                outL *= softScale;
                outR *= softScale;
            }

            Unsafe.Add(ref samplesRef, i) = outL;
            Unsafe.Add(ref samplesRef, i + 1) = outR;
        }

        for (; i < length; i++)
        {
            float gain = crossfader.AdvanceFrame(1);
            float amplified = Unsafe.Add(ref samplesRef, i) * gain;
            float absAmp = MathF.Abs(amplified);

            float neededGain = absAmp > KneeStart
                ? KneeStart * Helpers.SpanMathHelper.FastReciprocal(absAmp)
                : 1.0f;

            if (neededGain < targetEnv)
            {
                targetEnv = neededGain;
                holdRem = holdMax;
            }
            else if (holdRem > 0)
            {
                holdRem--;
            }
            else
            {
                targetEnv = 1.0f + releaseCoef * (targetEnv - 1.0f);
            }

            float coef = targetEnv < envGain ? attackCoef : releaseCoef;
            envGain = targetEnv + coef * (envGain - targetEnv);

            float outM = amplified * envGain;
            float outAbs = absAmp * envGain;
            if (outAbs > KneeStart)
                outM = MathF.CopySign(SoftCeilingPositive(outAbs), outM);

            Unsafe.Add(ref samplesRef, i) = outM;
        }

        _envelopeGain = envGain;
        _targetEnvelope = targetEnv;
        _holdFramesRemaining = holdRem;
    }

    /// <summary>
    /// Сбрасывает envelope в исходное состояние (gain = 1.0).
    /// </summary>
    public void Reset()
    {
        _envelopeGain = 1.0f;
        _targetEnvelope = 1.0f;
        _holdFramesRemaining = 0;
    }
}