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

    /// <summary>Attack time в миллисекундах.</summary>
    /// <remarks>
    /// 5ms — минимальное время защиты от клиппинга.
    /// Короче → артефакты от слишком агрессивного gain reduction.
    /// Длиннее → transient'ы успевают клипнуть до срабатывания limiter'а.
    /// </remarks>
    private const float AttackMs = 5f;

    /// <summary>Release time в миллисекундах.</summary>
    /// <remarks>
    /// 150ms — компромисс между плавностью и отзывчивостью.
    /// Короче (50ms) → заметный pumping на плотных треках.
    /// Длиннее (300ms) → медленное восстановление после пиков.
    /// </remarks>
    private const float ReleaseMs = 150f;

    /// <summary>Epsilon для защиты от деления на ноль.</summary>
    private const float Epsilon = 1e-7f;

    #endregion

    #region State

    /// <summary>
    /// Текущий gain envelope [0, 1]. Значение меньше 1.0 означает
    /// что limiter активен (gain reduction). Несёт состояние между chunk'ами.
    /// </summary>
    private float _envelopeGain;

    /// <summary>
    /// Возвращает текущее состояние ослабления огибающей лимитера.
    /// Значение 1.0f означает, что лимитер полностью восстановился и не ослабляет сигнал.
    /// </summary>
    public float EnvelopeGain => _envelopeGain;

    /// <summary>
    /// Attack коэффициент (коэффициент экспоненциального сглаживания).
    /// Вычисляется один раз в конструкторе: <c>exp(-1 / (sampleRate × attackMs/1000))</c>.
    /// </summary>
    private readonly float _attackCoef;

    /// <summary>
    /// Release коэффициент аналогично <see cref="_attackCoef"/>.
    /// </summary>
    private readonly float _releaseCoef;

    #endregion

    /// <summary>
    /// Создаёт True Peak Limiter с указанным sample rate.
    /// Attack/Release коэффициенты вычисляются на основе стандартных audio формул.
    /// </summary>
    /// <param name="sampleRate">Sample rate аудио (Гц).</param>
    public TruePeakLimiter(int sampleRate)
    {
        _envelopeGain = 1.0f;

        // Стандартная формула для time-constant RC фильтра:
        // coef = exp(-1 / (sampleRate * timeMs / 1000))
        // При coef близком к 1 — медленная реакция (release).
        // При coef близком к 0 — быстрая реакция (attack).
        float attackSamples = sampleRate * AttackMs / 1000f;
        float releaseSamples = sampleRate * ReleaseMs / 1000f;

        _attackCoef = MathF.Exp(-1f / attackSamples);
        _releaseCoef = MathF.Exp(-1f / releaseSamples);
    }

    /// <summary>
    /// Обрабатывает interleaved PCM буфер in-place.
    /// Применяет crossfaded gain и True Peak ограничение с Attack/Release envelope.
    /// </summary>
    /// <remarks>
    /// <para><b>Per-sample порядок обработки:</b></para>
    /// <list type="number">
    ///   <item>Получить текущий crossfade gain через <see cref="GainCrossfader.Advance"/>.</item>
    ///   <item>Применить gain к сэмплу.</item>
    ///   <item>Вычислить желаемый envelope gain для защиты от клиппинга.</item>
    ///   <item>Обновить envelope через attack/release.</item>
    ///   <item>Записать: <c>output = amplified × envelopeGain</c>.</item>
    /// </list>
    /// <para><b>Почему crossfader передаётся по ref:</b> GainCrossfader — value type struct
    /// с внутренним состоянием (<c>_remainingSamples</c>). Передача по ref позволяет
    /// <see cref="GainCrossfader.Advance"/> мутировать состояние без boxing/copy.</para>
    /// </remarks>
    /// <param name="samples">Interleaved PCM сэмплы (in-place обработка).</param>
    /// <param name="crossfader">Ссылка на crossfader для per-sample gain interpolation.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(Span<float> samples, ref GainCrossfader crossfader)
    {
        int length = samples.Length;
        if (length == 0) return;

        ref float samplesRef = ref MemoryMarshal.GetReference(samples);

        float envGain = _envelopeGain;
        float attackCoef = _attackCoef;
        float releaseCoef = _releaseCoef;

        int i = 0;
        int pairLimit = length & ~1;

        // Stereo-linked обработка: вычисляем единый пик по обоим каналам фрейма (max(|L|, |R|))
        // и применяем одинаковый envGain к L и R, исключая щелчки в правом канале и смещение панорамы.
        for (; i < pairLimit; i += 2)
        {
            float gain = crossfader.AdvanceFrame(2);
            float ampL = Unsafe.Add(ref samplesRef, i) * gain;
            float ampR = Unsafe.Add(ref samplesRef, i + 1) * gain;

            float absPeak = MathF.Max(MathF.Abs(ampL), MathF.Abs(ampR));

            float desiredEnv = absPeak > Ceiling
                ? Ceiling / (absPeak + Epsilon)
                : 1.0f;

            envGain = desiredEnv < envGain
                  ? attackCoef * envGain + (1f - attackCoef) * desiredEnv
                  : releaseCoef * envGain + (1f - releaseCoef) * desiredEnv;

            Unsafe.Add(ref samplesRef, i) = Math.Clamp(ampL * envGain, -Ceiling, Ceiling);
            Unsafe.Add(ref samplesRef, i + 1) = Math.Clamp(ampR * envGain, -Ceiling, Ceiling);
        }

        for (; i < length; i++)
        {
            float gain = crossfader.AdvanceFrame(1);
            float amplified = Unsafe.Add(ref samplesRef, i) * gain;
            float absAmp = MathF.Abs(amplified);

            float desiredEnv = absAmp > Ceiling
                ? Ceiling / (absAmp + Epsilon)
                : 1.0f;

            envGain = desiredEnv < envGain
                ? attackCoef * envGain + (1f - attackCoef) * desiredEnv
                : releaseCoef * envGain + (1f - releaseCoef) * desiredEnv;

            Unsafe.Add(ref samplesRef, i) = Math.Clamp(amplified * envGain, -Ceiling, Ceiling);
        }

        _envelopeGain = envGain;
    }

    /// <summary>
    /// Сбрасывает envelope в исходное состояние (gain = 1.0).
    /// Вызывается при seek или смене трека для предотвращения
    /// применения старого envelope к новой позиции.
    /// </summary>
    public void Reset() => _envelopeGain = 1.0f;
}