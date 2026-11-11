using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Linear gain interpolator to eliminate clicks and stereo-image shifts
/// when changing the target normalization gain or volume value.
///
/// <para><b>Operation principle:</b></para>
/// <para>When the target gain changes, call <see cref="SetTarget"/>.
/// Subsequent calls to <see cref="Advance"/> or <see cref="AdvanceFrame"/> return a linearly
/// interpolated value from <c>fromGain</c> to <c>toGain</c>
/// over <c>fadeSamples</c> samples. After interpolation completes,
/// constant <c>toGain</c> is returned.</para>
///
/// <para><b>Fade duration thresholds:</b></para>
/// <list type="bullet">
///   <item>Sudden change (|delta| ≥ <see cref="SuddenChangeThreshold"/>):
///     <see cref="SuddenFadeMs"/> ms — covers lock events, reset, settings changes.</item>
///   <item>Gradual change: <see cref="GradualFadeMs"/> ms —
///     normal convergence of provisional gain.</item>
/// </list>
///
/// <para><b>Zero-alloc value type.</b> All state is stored inside the struct.
/// Stored as a field in <see cref="AudioPipeline"/>.</para>
///
/// <para><b>Single writer:</b> used exclusively from the backend fill thread
/// in <c>AudioCallback</c>. No write contention.</para>
/// </summary>
internal struct GainCrossfader
{
    #region Constants

    /// <summary>
    /// Gain change threshold to identify a "sudden" transition.
    /// A change ≥ 5% is considered sudden and requires a long fade.
    /// </summary>
    private const float SuddenChangeThreshold = 0.05f;

    /// <summary>Fade duration for sudden gain changes (ms).</summary>
    private const float SuddenFadeMs = 300f;

    /// <summary>Fade duration for gradual gain changes (ms).</summary>
    private const float GradualFadeMs = 50f;

    #endregion

    #region State

    private float _currentGain;
    private float _startGain;
    private float _targetGain;
    private float _invTotalFadeSamples;
    private int _remainingSamples;

    #endregion

    /// <summary>
    /// Creates a crossfader with an initial gain value.
    /// </summary>
    /// <param name="initialGain">Initial gain (typically 1.0 for a new track).</param>
    public GainCrossfader(float initialGain)
    {
        _currentGain = initialGain;
        _startGain = initialGain;
        _targetGain = initialGain;
        _invTotalFadeSamples = 0f;
        _remainingSamples = 0;
    }

    /// <summary>
    /// Current gain value (as of the last <see cref="Advance"/>).
    /// Used to initialize the next fade.
    /// </summary>
    public readonly float CurrentGain => _currentGain;

    /// <summary>
    /// <c>true</c> if interpolation is active (has not reached <c>targetGain</c> yet).
    /// </summary>
    public readonly bool IsActive => _remainingSamples > 0;

    /// <summary>
    /// Sets a new target gain and begins interpolation if the value has changed.
    /// </summary>
    /// <remarks>
    /// <para>If the new target is identical to the current one (|delta| &lt; 0.0001) — no-op.
    /// If interpolation is already in progress, the new target interrupts it, starting
    /// a new fade from the current position — prevents transition overlap.</para>
    /// </remarks>
    /// <param name="newTarget">New target gain value.</param>
    /// <param name="sampleRate">Sample rate for calculating fade length in samples.</param>
    /// <param name="channels">Number of channels (fade is calculated in stereo samples).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTarget(float newTarget, int sampleRate, int channels)
    {
        if (MathF.Abs(newTarget - _targetGain) < 0.0001f) return;

        float delta = MathF.Abs(newTarget - _currentGain);
        float fadeMs = delta >= SuddenChangeThreshold ? SuddenFadeMs : GradualFadeMs;

        _startGain = _currentGain;
        _targetGain = newTarget;
        int totalSamples = (int)(sampleRate * channels * fadeMs / 1000f);
        _remainingSamples = totalSamples;
        _invTotalFadeSamples = totalSamples > 0 ? 1.0f / totalSamples : 0f;
    }

    /// <summary>
    /// Returns the gain for the current sample and advances interpolation by 1 sample.
    /// </summary>
    /// <returns>Current interpolated gain.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Advance()
    {
        return AdvanceFrame(1);
    }

    /// <summary>
    /// Returns a single gain for the entire multichannel frame and advances interpolation by <paramref name="channels"/> samples,
    /// avoiding phase and amplitude skew between left and right channels.
    /// </summary>
    /// <param name="channels">Number of channels in a single audio frame.</param>
    /// <returns>Current interpolated gain for the frame.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float AdvanceFrame(int channels)
    {
        if (_remainingSamples <= 0)
        {
            _currentGain = _targetGain;
            return _targetGain;
        }

        int step = Math.Min(channels, _remainingSamples);
        _remainingSamples -= step;

        if (_remainingSamples <= 0)
        {
            _currentGain = _targetGain;
            return _targetGain;
        }

        float t = 1f - _remainingSamples * _invTotalFadeSamples;
        _currentGain = _startGain + (_targetGain - _startGain) * t;
        return _currentGain;
    }

    /// <summary>
    /// Resets the crossfader to its initial state with the specified gain.
    /// Called on seek or device loss.
    /// </summary>
    /// <param name="gain">Gain to set immediately (without fade).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(float gain)
    {
        _currentGain = gain;
        _startGain = gain;
        _targetGain = gain;
        _invTotalFadeSamples = 0f;
        _remainingSamples = 0;
    }
}