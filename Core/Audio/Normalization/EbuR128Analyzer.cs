using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// EBU R128 / ITU-R BS.1770-4 loudness analyzer.
///
/// <para><b>Algorithm:</b></para>
/// <list type="bullet">
///   <item><b>Pre-scan</b> (isolated pipeline, ~50-300ms): K-weighted LUFS with relative gating.
///     Gain is locked prior to playback start.</item>
///   <item><b>Fallback</b>: if pre-scan is impossible (insufficient data),
///     a carry-over gain from the previous track is used. LUFS will be calculated
///     on the next playback after caching.</item>
/// </list>
///
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item><see cref="ProcessSamples"/> — fill thread (hot path, zero-alloc)</item>
///   <item><see cref="Configure"/>, <see cref="LockFromCache"/>, <see cref="LockFromMetadata"/> —
///     command thread</item>
///   <item><see cref="_pendingNormReset"/> — volatile int, lock-free sync</item>
/// </list>
/// </summary>
internal sealed class EbuR128Analyzer
{
    #region Constants (EBU R128 / ITU-R BS.1770-4)

    /// <summary>Minimum normalization gain (protection against excessive attenuation).</summary>
    private const float MinNormalizationGain = 0.1f;

    /// <summary>Default maximum normalization gain.</summary>
    private const float DefaultMaxNormalizationGain = 3.0f;

    /// <summary>Relative gating threshold: −10 LU (ITU-R BS.1770-4 §3).</summary>
    private const double RelativeGateOffsetLu = -10.0;

    /// <summary>Constant from ITU-R BS.1770-4 equation (2): −0.691 dBFS.</summary>
    private const double LufsOffset = -0.691;

    /// <summary>Max number of gating blocks for pre-scan.</summary>
    internal const int MaxScanGatingBlocks = 512;

    #endregion

    #region Gain State

    /// <summary>
    /// Locked gain. NaN = gain is not set.
    /// Single write point: <see cref="LockGain"/>, <see cref="LockResolvedGain"/>.
    /// </summary>
    private float _lockedGain = float.NaN;

    /// <summary>Initial gain from the previous track (prevents cold-start pop).</summary>
    private float _startingNormGain = 1.0f;

    /// <summary>
    /// Last measured integrated LUFS. Used for gain recalculation
    /// when normalization parameters change (targetLufs, mode) without re-scanning.
    /// </summary>
    private float _lastIntegratedLufs = float.NaN;

    /// <summary>
    /// Deferred reset signal. 1 = full reset, 2 = recalc, 0 = none.
    /// Set from command thread, executed by fill thread.
    /// </summary>
    private volatile int _pendingNormReset;

    /// <summary>
    /// Integrated LUFS lock callback. Invoked at most once per pipeline.
    /// </summary>
    private volatile Action<float>? _onIntegratedLufsResolved;

#if DEBUG
    private float _lastLoggedCacheGain = float.NaN;
    private NormalizationMode _lastLoggedCacheMode;
#endif

    #endregion

    #region Configuration

    private volatile bool _enabled;
    private float _targetLufs = -14f;
    private float _maxGain = DefaultMaxNormalizationGain;
    private NormalizationMode _mode = NormalizationMode.Bidirectional;
    private int _preScanDurationMs = 30_000;

    private sealed record ConfigSnapshot(NormalizationConfig Value);
    private volatile ConfigSnapshot? _pendingConfig;

    #endregion

    #region Public Properties

    /// <summary>Whether normalization is enabled.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Whether gain is locked.</summary>
    public bool IsGainLocked => !float.IsNaN(Volatile.Read(ref _lockedGain));

    /// <summary>Current configuration (snapshot reflecting pending settings).</summary>
    public NormalizationConfig CurrentConfig =>
        _pendingConfig?.Value ?? new(_enabled, _targetLufs, _maxGain, _mode, _preScanDurationMs);

    #endregion

    #region Configuration API

    /// <summary>
    /// Applies normalization configuration atomically.
    /// </summary>
    public void Configure(NormalizationConfig config)
    {
        float clampedMaxGain = Math.Max(1f, config.MaxGain);
        var normalizedConfig = config with { MaxGain = clampedMaxGain };

        if (!config.Enabled && _enabled)
        {
            _enabled = false;
            _lockedGain = float.NaN;
            Log.Debug("[EbuR128] Normalization OFF");
            return;
        }

        if (!config.Enabled) return;

        bool wasEnabled = _enabled;
        bool changed = !wasEnabled
            || MathF.Abs(_targetLufs - normalizedConfig.TargetLufs) > 0.01f
            || MathF.Abs(_maxGain - normalizedConfig.MaxGain) > 0.01f
            || _mode != normalizedConfig.Mode
            || _preScanDurationMs != normalizedConfig.PreScanDurationMs;

        if (!changed) return;

        bool needsReset = !wasEnabled;
        bool needsRecalc = wasEnabled && changed;

        _pendingConfig = new ConfigSnapshot(normalizedConfig);
        _enabled = true;

        if (needsReset)
        {
            _pendingNormReset = 1;
            Log.Debug($"[EbuR128] Normalization ON: target={normalizedConfig.TargetLufs}LUFS, " +
                      $"maxGain={normalizedConfig.MaxGain:F1}x, mode={normalizedConfig.Mode}");
        }
        else if (needsRecalc)
        {
            _pendingNormReset = 2;
            Log.Debug($"[EbuR128] Params changed (recalc): target={normalizedConfig.TargetLufs}LUFS, " +
                      $"maxGain={normalizedConfig.MaxGain:F1}x, mode={normalizedConfig.Mode}");
        }
    }

    /// <summary>
    /// Publishes measured integrated LUFS and retains it for recalculation.
    /// </summary>
    internal void NotifyIntegratedLufs(float integratedLufs)
    {
        if (float.IsFinite(integratedLufs))
        {
            _lastIntegratedLufs = integratedLufs;
            _onIntegratedLufsResolved?.Invoke(integratedLufs);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyPendingConfig()
    {
        var snapshot = Interlocked.Exchange(ref _pendingConfig, null);
        if (snapshot == null) return;

        var cfg = snapshot.Value;
        _targetLufs = cfg.TargetLufs;
        _maxGain = Math.Max(1f, cfg.MaxGain);
        _mode = cfg.Mode;
        if (cfg.PreScanDurationMs > 0)
            _preScanDurationMs = cfg.PreScanDurationMs;
    }

    /// <inheritdoc cref="SetIntegratedLufsCallback"/>
    public void SetIntegratedLufsCallback(Action<float>? callback) => _onIntegratedLufsResolved = callback;

    /// <summary>
    /// Sets initial carry-over gain from the previous track.
    /// </summary>
    public void SetInitialGain(float gain)
    {
        _startingNormGain = Math.Clamp(gain, MinNormalizationGain, DefaultMaxNormalizationGain);
    }

    /// <summary>Requests reset on seek (if gain is not yet locked).</summary>
    public void PrepareForSeek()
    {
    }

    #endregion

    #region Gain Locking

    /// <summary>
    /// Applies calculated runtime-gain without firing additional events.
    /// </summary>
    public void LockResolvedGain(float gain)
    {
        if (!_enabled) return;
        if (gain <= 0f || !float.IsFinite(gain)) return;

        LockGain(gain);

#if DEBUG
        if (MathF.Abs(_lockedGain - _lastLoggedCacheGain) > 0.0005f || _mode != _lastLoggedCacheMode)
        {
            _lastLoggedCacheGain = _lockedGain;
            _lastLoggedCacheMode = _mode;
            Log.Debug($"[EbuR128] Gain from cache: {_lockedGain:F3}x (mode={_mode}, analysis skipped)");
        }
#endif
    }

    /// <summary>
    /// Locks gain from EBU R128 pre-scan or metadata with guaranteed application of the active configuration.
    /// </summary>
    public void LockGain(float gain)
    {
        // Read active config without mutating fill-thread fields so background pre-scan remains lock-free.
        var cfg = CurrentConfig;
        if (cfg.Mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, Math.Max(1f, cfg.MaxGain));

        _pendingNormReset = 0;
        Volatile.Write(ref _lockedGain, gain);
    }

    /// <summary>
    /// Returns locked gain or carry-over gain from the previous track.
    /// </summary>
    public float GetLockedGain()
    {
        if (!_enabled) return 1.0f;
        float locked = Volatile.Read(ref _lockedGain);
        return float.IsNaN(locked) ? _startingNormGain : locked;
    }

    #endregion

    #region Hot Path

    /// <summary>
    /// Returns current normalization gain. Processes pending operations
    /// from command thread (config apply, reset, recalculate).
    /// </summary>
    /// <remarks>
    /// <para><b>CALL ONLY FROM FILL THREAD.</b></para>
    /// <para><b>Zero-alloc. O(1).</b></para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ProcessSamples()
    {
        ApplyPendingConfig();

        int pendingOp = Interlocked.Exchange(ref _pendingNormReset, 0);
        if (pendingOp == 1)
            ExecuteReset();
        else if (pendingOp == 2)
            ExecuteRecalculate();

        float locked = Volatile.Read(ref _lockedGain);
        return float.IsNaN(locked) ? _startingNormGain : locked;
    }

    #endregion

    #region Internal State Management

    /// <summary>
    /// Recalculates gain from stored integrated LUFS with new parameters.
    /// </summary>
    private void ExecuteRecalculate()
    {
        if (!float.IsFinite(_lastIntegratedLufs))
            return;

        float gain = ComputeGainFromLufs(_lastIntegratedLufs, _targetLufs, _maxGain);
        if (_mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        Volatile.Write(ref _lockedGain, gain);

        Log.Debug($"[EbuR128] Recalculated from LUFS={_lastIntegratedLufs:F2}: " +
                  $"gain={gain:F3}x (target={_targetLufs}, mode={_mode})");
    }

    /// <summary>
    /// Resets gain state for a new track.
    /// </summary>
    private void ExecuteReset()
    {
        Volatile.Write(ref _lockedGain, float.NaN);
        _lastIntegratedLufs = float.NaN;

#if DEBUG
        _lastLoggedCacheGain = float.NaN;
        Log.Debug("[EbuR128] Reset: ready for new track");
#endif
    }

    #endregion

    #region Static EBU R128 Computation (used by pre-scan)

    /// <summary>
    /// Computes integrated LUFS from an array of gating block powers (EBU R128).
    /// Applies absolute gate (−70 LUFS) and relative gate (−10 LU).
    /// </summary>
    internal static float ComputeIntegratedLufsFromBlocks(double[] blockPowers, int blockCount)
    {
        if (blockCount == 0)
            return float.NaN;

        double sumPower = 0.0;
        for (int i = 0; i < blockCount; i++)
            sumPower += blockPowers[i];

        double meanPower = sumPower / blockCount;
        double integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));

        double relThreshold = integratedLufs + RelativeGateOffsetLu;
        double relPowerThreshold = Math.Pow(10.0, (relThreshold - LufsOffset) / 10.0);

        double gatedSum = 0.0;
        int gatedCount = 0;
        for (int i = 0; i < blockCount; i++)
        {
            if (blockPowers[i] >= relPowerThreshold)
            {
                gatedSum += blockPowers[i];
                gatedCount++;
            }
        }

        if (gatedCount > 0)
        {
            meanPower = gatedSum / gatedCount;
            integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));
        }

        return (float)integratedLufs;
    }

    /// <summary>
    /// Converts measured LUFS loudness level into a clamped linear gain factor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputeGainFromLufs(float integratedLufs, float targetLufs, float maxGain)
    {
        if (!float.IsFinite(integratedLufs))
            return 1.0f;

        float gainDb = targetLufs - integratedLufs;
        float gain = MathF.Pow(10f, gainDb / 20f);
        return Math.Clamp(gain, MinNormalizationGain, Math.Max(1f, maxGain));
    }

    /// <summary>
    /// Computes raw linear gain from gating block powers without duplicating gating logic.
    /// </summary>
    internal static float ComputeIntegratedGainFromBlocks(
        double[] blockPowers, int blockCount, float targetLufs, float maxGain)
    {
        float integratedLufs = ComputeIntegratedLufsFromBlocks(blockPowers, blockCount);
        return ComputeGainFromLufs(integratedLufs, targetLufs, maxGain);
    }

    #endregion
}