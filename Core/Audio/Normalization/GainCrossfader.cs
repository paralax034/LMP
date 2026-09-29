using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Per-sample линейный интерполятор gain для устранения щелчков
/// при смене целевого значения gain нормализации или громкости.
///
/// <para><b>Принцип работы:</b></para>
/// <para>При изменении целевого gain вызывается <see cref="SetTarget"/>.
/// Последующие вызовы <see cref="Advance"/> возвращают линейно
/// интерполированное значение от <c>fromGain</c> до <c>toGain</c>
/// на протяжении <c>fadeSamples</c> сэмплов. После завершения интерполяции
/// возвращается константный <c>toGain</c>.</para>
///
/// <para><b>Пороги fade duration:</b></para>
/// <list type="bullet">
///   <item>Резкое изменение (|delta| ≥ <see cref="SuddenChangeThreshold"/>):
///     <see cref="SuddenFadeMs"/> мс — покрывает lock события, reset, смену настроек.</item>
///   <item>Плавное изменение: <see cref="GradualFadeMs"/> мс —
///     нормальная конвергенция provisional gain.</item>
/// </list>
///
/// <para><b>Zero-alloc value type.</b> Всё состояние хранится внутри struct.
/// Хранится как поле в <see cref="AudioPipeline"/>.</para>
///
/// <para><b>Single writer:</b> используется исключительно из fill thread
/// backend'а в <c>AudioCallback</c>. Нет конкуренции по записи.</para>
/// </summary>
public struct GainCrossfader
{
    #region Constants

    /// <summary>
    /// Порог изменения gain для определения «резкого» перехода.
    /// Изменение ≥ 5% считается резким и требует длинного fade.
    /// </summary>
    private const float SuddenChangeThreshold = 0.05f;

    /// <summary>Длительность fade при резком изменении gain (мс).</summary>
    private const float SuddenFadeMs = 300f;

    /// <summary>Длительность fade при плавном изменении gain (мс).</summary>
    private const float GradualFadeMs = 50f;

    #endregion

    #region State

    private float _currentGain;
    private float _startGain;
    private float _targetGain;
    private int _remainingSamples;
    private int _totalFadeSamples;

    #endregion

    /// <summary>
    /// Создаёт crossfader с начальным значением gain.
    /// </summary>
    /// <param name="initialGain">Начальный gain (обычно 1.0 для нового трека).</param>
    public GainCrossfader(float initialGain)
    {
        _currentGain = initialGain;
        _startGain = initialGain;
        _targetGain = initialGain;
        _remainingSamples = 0;
        _totalFadeSamples = 0;
    }

    /// <summary>
    /// Текущее значение gain (на момент последнего <see cref="Advance"/>).
    /// Используется для инициализации следующего fade.
    /// </summary>
    public readonly float CurrentGain => _currentGain;

    /// <summary>
    /// <c>true</c> если интерполяция активна (ещё не достигли <c>targetGain</c>).
    /// </summary>
    public readonly bool IsActive => _remainingSamples > 0;

    /// <summary>
    /// Устанавливает новый целевой gain и начинает interpolation если значение изменилось.
    /// </summary>
    /// <remarks>
    /// <para>Если новый target идентичен текущему (|delta| &lt; 0.0001) — no-op.
    /// Если интерполяция уже идёт, новый target прерывает её, начиная
    /// новый fade с текущей позиции — предотвращает наложение переходов.</para>
    /// </remarks>
    /// <param name="newTarget">Новое целевое значение gain.</param>
    /// <param name="sampleRate">Sample rate для вычисления длины fade в сэмплах.</param>
    /// <param name="channels">Количество каналов (fade считается в stereo samples).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTarget(float newTarget, int sampleRate, int channels)
    {
        if (MathF.Abs(newTarget - _targetGain) < 0.0001f) return;

        float delta = MathF.Abs(newTarget - _currentGain);
        float fadeMs = delta >= SuddenChangeThreshold ? SuddenFadeMs : GradualFadeMs;

        _startGain = _currentGain;
        _targetGain = newTarget;
        _totalFadeSamples = (int)(sampleRate * channels * fadeMs / 1000f);
        _remainingSamples = _totalFadeSamples;
    }

    /// <summary>
    /// Возвращает gain для текущего сэмпла и продвигает интерполяцию на 1 сэмпл.
    /// </summary>
    /// <returns>Текущий interpolated gain.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Advance()
    {
        if (_remainingSamples <= 0)
        {
            _currentGain = _targetGain;
            return _targetGain;
        }

        float t = 1f - (float)_remainingSamples / _totalFadeSamples;
        _currentGain = _startGain + (_targetGain - _startGain) * t;
        _remainingSamples--;
        return _currentGain;
    }

    /// <summary>
    /// Сбрасывает crossfader в начальное состояние с указанным gain.
    /// Вызывается при seek или потере устройства.
    /// </summary>
    /// <param name="gain">Gain для немедленной установки (без fade).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(float gain)
    {
        _currentGain = gain;
        _startGain = gain;
        _targetGain = gain;
        _remainingSamples = 0;
        _totalFadeSamples = 0;
    }
}