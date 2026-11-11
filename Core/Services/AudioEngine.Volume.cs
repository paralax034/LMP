namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Volume State

    private int _volumePercent;
    private float _currentGain;
    private readonly Lock _volumeLock = new();
    private int _lastSavedVolume = -1;
    private readonly SemaphoreSlim _persistenceSignal = new(0, 1);

    private const int VolumeSaveDebounceMs = 2000;

    #endregion

    #region Public API

    /// <summary>Returns the current volume percentage.</summary>
    public float GetVolume() => _volumePercent;

    /// <summary>Sets the volume instantly.</summary>
    public void SetVolumeInstant(float value)
    {
        int clamped;
        lock (_volumeLock)
        {
            clamped = Math.Clamp(
                (int)Math.Round(value), 0,
                Math.Max(_library.Settings.MaxVolumeLimit, 100));
            _volumePercent = clamped;
        }
        ApplyGainToPipeline();
        if (clamped != Volatile.Read(ref _lastSavedVolume))
            NotifyPersistenceNeeded();
    }

    /// <summary>Handles changes to the maximum volume limit.</summary>
    public void OnMaxVolumeLimitChanged(int newMaxVolume)
    {
        lock (_volumeLock)
        {
            if (_volumePercent > newMaxVolume) _volumePercent = newMaxVolume;
        }
        ApplyGainToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(newMaxVolume));
    }

    /// <summary>Recalculates gain after audio settings change.</summary>
    public void UpdateAudioSettings()
    {
        ApplyGainToPipeline();
        ApplyNormalizationToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
    }

    /// <summary>Initializes volume from settings at startup or after database load.</summary>
    private void InitializeVolumeFromSettings()
    {
        var settings = _library.Settings;
        lock (_volumeLock)
        {
            _volumePercent = settings.Volume >= 0
                ? Math.Clamp(settings.Volume, 0, Math.Max(settings.MaxVolumeLimit, 100))
                : 50;
            _lastSavedVolume = _volumePercent;
        }
        ApplyGainToPipeline();
    }

    #endregion

    #region Internal Computation & Loop

    /// <summary>
    /// Signals the background loop to flush updated volume or LUFS to the database without periodic polling.
    /// </summary>
    private void NotifyPersistenceNeeded()
    {
        if (_persistenceSignal.CurrentCount == 0)
        {
            try { _persistenceSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Single source of truth for calculating final gain.
    /// </summary>
    private float ComputeFinalGain()
    {
        var settings = _library.Settings;
        float gain = ComputeGain(_volumePercent, Math.Max(settings.MaxVolumeLimit, 100), settings.Audio);
        return Math.Clamp(gain, 0f, MaxGain);
    }

    /// <summary>Applies volume gain to the backend.</summary>
    private void ApplyGainToPipeline()
    {
        float gain = ComputeFinalGain();
        _currentGain = gain;
        _player.SetVolumeGain(gain);
    }

    private static float ComputeGain(int volumePercent, int maxVolume, AudioSettings audioSettings)
    {
        if (volumePercent <= 0) return 0f;

        if (audioSettings.VolumeBoostEnabled)
        {
            int normalCeiling = Math.Min(maxVolume, VolumeNormalRange);
            if (volumePercent <= normalCeiling)
                return ApplyVolumeCurve((float)volumePercent / normalCeiling, audioSettings.VolumeCurve);

            return 1.0f + (float)(volumePercent - normalCeiling) / VolumeNormalRange;
        }

        return ApplyVolumeCurve((float)volumePercent / maxVolume, audioSettings.VolumeCurve);
    }

    private static float ApplyVolumeCurve(float t, VolumeCurveType curve)
    {
        t = Math.Clamp(t, 0f, 1f);
        return curve switch
        {
            VolumeCurveType.Linear => t,
            VolumeCurveType.Quadratic => t * t,
            VolumeCurveType.Logarithmic => MathF.Log2(1f + t),
            VolumeCurveType.Cubic => t * t * t,
            VolumeCurveType.SpeedOfLight => (MathF.Exp(t * 2f) - 1f) / (MathF.Exp(2f) - 1f),
            _ => t * t
        };
    }

    /// <summary>
    /// Event-driven loop for persisting volume and normalization to the DB.
    /// Sleeps with 0 context switches until an actual volume change or new LUFS measurements arrive.
    /// </summary>
    private async Task VolumeSaveLoopAsync()
    {
        try
        {
            while (!_lifetimeCts.Token.IsCancellationRequested)
            {
                await _persistenceSignal.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);

                if (!await _lifetimeCts.Token.DelayNoThrowAsync(VolumeSaveDebounceMs, continueOnCapturedContext: false).ConfigureAwait(false))
                    break;

                int currentVol;
                lock (_volumeLock)
                {
                    currentVol = _volumePercent;
                }

                if (currentVol != _lastSavedVolume)
                {
                    int prevVol = _lastSavedVolume;
                    _lastSavedVolume = currentVol;
                    _library.UpdateSettings(s => s.Volume = currentVol);
                    Log.Info($"[AudioEngine] Volume saved: {prevVol}% -> {currentVol}% (gain={_currentGain:F4}x)");
                }

                await FlushPendingNormalizationWritesAsync(_lifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion
}