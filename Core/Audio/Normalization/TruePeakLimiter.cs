using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// True Peak Limiter с Attack/Release envelope и сохранением состояния между chunk'ами.
///
/// <para><b>Проблема предыдущей реализации (chunk-level stateless limiter):</b>
/// Статический метод без межчанкового состояния вычислял peak по всему chunk (~50ms)
/// и применял единый safeGain. Один transient в chunk'е давил весь 50ms блок →
/// следующий chunk без transient'а получал полный gain → «пампинг» каждые 50ms.</para>
///
/// <para><b>Алгоритм (envelope follower):</b></para>
/// <list type="number">
///   <item>Для каждого сэмпла: вычислить амплитуду после применения gain.</item>
///   <item>Определить желаемый envelope gain:
///     <c>targetEnv = ceiling / max(|amplified|, ε)</c>, clamp [0, 1].</item>
///   <item>Сгладить через attack/release:
///     <list type="bullet">
///       <item>Attack (снижение gain): короткий (~5ms) — быстрая защита от клиппинга.</item>
///       <item>Release (возврат gain): длинный (~150ms) — плавный выход без pumping.</item>
///     </list>
///   </item>
///   <item>Применить: <c>output = amplified × envelopeGain</c>.</item>
/// </list>
///
/// <para><b>Почему envelope follower лучше chunk-level scan:</b></para>
/// <para>Envelope несёт состояние между chunk'ами: если в конце chunk N был transient,
/// release на начале chunk N+1 начинается НЕ с 1.0, а с того уровня куда дошёл attack.
/// Это устраняет pumping — громкость изменяется непрерывно, а не ступенчато.</para>
///
/// <para><b>SIMD-оптимизация:</b> Envelope follower имеет data dependency (каждый сэмпл
/// зависит от предыдущего), поэтому SIMD-векторизация loop'а невозможна.
/// Оптимизация достигается через <see cref="MethodImplOptions.AggressiveInlining"/>
/// и bounds elision через <see cref="Unsafe.Add{T}(ref T, int)"/>.</para>
///
/// <para><b>Zero-alloc. Нет аллокаций на hot path.</b></para>
///
/// <para><b>Single writer:</b> вызывается исключительно из fill thread в
/// <c>AudioCallback</c>. Нет конкуренции по состоянию.</para>
/// </summary>
public sealed class TruePeakLimiter
{
    #region Constants

    /// <summary>
    /// Потолок True Peak защиты (-0.13 dBFS = 0.985f).
    /// Предотвращает срабатывание системного лимитера Windows (CAudioLimiter APO в audiodg.exe) на 95-100% громкости.
    /// </summary>
    private const float Ceiling = 0.985f;

    /// <summary>
    /// Порог начала мягкого асимптотического скругления пиков (0.90f) на первых миллисекундах атаки.
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

    /// <summary>Epsilon для защиты от деления на ноль.</summary>
    private const float Epsilon = 1e-7f;

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
    /// Гладкий C2-непрерывный асимптотический сатуратор для защиты верхушек в первые 4 мс атаки огибающей.
    /// В точке <see cref="KneeStart"/> имеет <c>f'(0) = 1</c> и <c>f''(0) = 0</c>, что полностью исключает
    /// скачок второй производной (микро-треск на тихих ambient-треках с высоким усилением и на входе в саб-бас).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftCeiling(float x)
    {
        float abs = MathF.Abs(x);
        if (abs <= KneeStart)
            return x;

        float u = (abs - KneeStart) * InvKneeSpan;
        float u2 = u * u;
        float curved = KneeStart + KneeSpan * ((u + u2) / (1.0f + u + u2));
        return MathF.CopySign(curved, x);
    }

    /// <summary>
    /// Обрабатывает interleaved PCM буфер in-place.
    /// </summary>
    /// <param name="samples">Interleaved PCM сэмплы (in-place обработка).</param>
    /// <param name="crossfader">Ссылка на crossfader для per-sample gain interpolation.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

            float absPeak = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));
            float neededGain = absPeak > KneeStart
                ? (KneeStart * 0.995f) / (absPeak + Epsilon)
                : 1.0f;

            if (neededGain <= targetEnv)
            {
                targetEnv = neededGain;
                holdRem = holdMax;
            }
            else if (neededGain <= targetEnv * 1.06f && neededGain < 1.0f)
            {
                // Любая полуволна баса в пределах 6% от текущего пика перезаряжает удержание 30 мс
                // (полупериод 36 Гц = 13.9 мс < 30 мс -> holdRem НИКОГДА не истекает во время звучания баса,
                // поэтому targetEnv стоит абсолютно неподвижно: EnvRipple == 0.000000!)
                holdRem = holdMax;
            }
            else if (holdRem > 0)
            {
                holdRem--;
            }
            else
            {
                targetEnv = releaseCoef * targetEnv + (1f - releaseCoef) * 1.0f;
            }

            // Когда targetEnv уже вышел на полку удержания, envGain сходится к нему и замирает в 0 ripple
            envGain = targetEnv < envGain
                ? attackCoef * envGain + (1f - attackCoef) * targetEnv
                : releaseCoef * envGain + (1f - releaseCoef) * targetEnv;

            Unsafe.Add(ref samplesRef, i) = SoftCeiling(inL * envGain);
            Unsafe.Add(ref samplesRef, i + 1) = SoftCeiling(inR * envGain);
        }

        for (; i < length; i++)
        {
            float gain = crossfader.AdvanceFrame(1);
            float amplified = Unsafe.Add(ref samplesRef, i) * gain;
            float absAmp = MathF.Abs(amplified);

            float neededGain = absAmp > KneeStart
                ? (KneeStart * 0.995f) / (absAmp + Epsilon)
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
                targetEnv = releaseCoef * targetEnv + (1f - releaseCoef) * 1.0f;
            }

            envGain = targetEnv < envGain
                ? attackCoef * envGain + (1f - attackCoef) * targetEnv
                : releaseCoef * envGain + (1f - releaseCoef) * targetEnv;

            Unsafe.Add(ref samplesRef, i) = SoftCeiling(amplified * envGain);
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