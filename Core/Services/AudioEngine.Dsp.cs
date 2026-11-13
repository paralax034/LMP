using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Normalization State

    private readonly Dictionary<string, (float IntegratedLufs, LoudnessSource Source)> _normalizationBatch =
        new(StringComparer.Ordinal);

    #endregion

    #region Pipeline Configuration

    /// <summary>
    /// Configures the pipeline before opening the gate: volume, normalization, and crossfader.
    /// </summary>
    private void ConfigurePipelineBeforeStart(AudioPipeline pipeline, string? trackId)
    {
        trackId ??= pipeline.StreamInfo.TrackId;

        float volumeGain = ComputeFinalGain();
        _currentGain = volumeGain;
        _player.SetVolumeGain(volumeGain);

        var audioSettings = _library.Settings.Audio;

        bool isFullCache = pipeline.Source is Audio.Sources.LocalFileSource;
        int preScanDurationMs = isFullCache ? PreScanDurationFullCacheMs : PreScanDurationStreamingMs;

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode,
            preScanDurationMs);

        pipeline.Analyzer.Configure(normConfig);

        Log.Debug($"[AudioEngine] Configuring pipeline for '{trackId}'. " +
                  $"Normalization: {normConfig.Enabled}, Mode: {normConfig.Mode}, " +
                  $"PreScan: {preScanDurationMs / 1000}s");

        if (normConfig.Enabled && !string.IsNullOrEmpty(trackId))
        {
            var registryTrack = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
            var currentTrack = CurrentTrack;
            var track = registryTrack ?? (currentTrack?.Id == trackId ? currentTrack : null);
            float resolvedGain = ResolveTrackNormalizationGain(track, trackId, normConfig, out var cacheEntry);

#if DEBUG
            if (track != null)
            {
                Log.Debug($"[AudioEngine] Track resolved: ID={track.Id}, Title='{track.Title}' " +
                          $"| Source: {(registryTrack != null ? "Registry" : "CurrentTrackFallback")} " +
                          $"| Integrated LUFS: {(track.HasIntegratedLufs ? track.IntegratedLufs.ToString("F2") : "NaN")} " +
                          $"| LUFS Source: {track.IntegratedLufsSource} " +
                          $"| Cache LUFS: {(cacheEntry?.IntegratedLufs is float clufs ? clufs.ToString("F2") : "null")} " +
                          $"| Cache LUFS Source: {(cacheEntry != null ? ((LoudnessSource)cacheEntry.IntegratedLufsSource).ToString() : "null")} " +
                          $"| Resolved Gain: {(float.IsNaN(resolvedGain) ? "NaN" : resolvedGain.ToString("F4"))}");
            }
#endif

            if (!float.IsNaN(resolvedGain))
            {
                pipeline.Analyzer.LockResolvedGain(resolvedGain);
                Log.Info(
                    $"[AudioEngine] Normalization gain locked from LUFS metadata: {resolvedGain:F4}x for {trackId}");
            }
            else if (track != null && !(pipeline.Source is Audio.Sources.CachingStreamSource
            {
                IsFullyBuffered: false
            }))
            {
                Log.Warn(
                    $"[AudioEngine] Normalization resolver returned NaN for {trackId}. EBU R128 Pre-scan is REQUIRED.");
            }
        }

        ApplyEqualizerToPipelineCore(pipeline, trackId, snapImmediately: true);
        pipeline.SnapCrossfaderToGain();
    }

    private static float ResolveTrackNormalizationGain(
        TrackInfo? track,
        string? trackId,
        in NormalizationConfig normConfig,
        out AudioCacheEntry? cacheEntry)
    {
        cacheEntry = !string.IsNullOrEmpty(trackId) ? FindNormalizationCacheEntry(trackId) : null;
        if (track != null && cacheEntry != null)
            TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

        if (!normConfig.Enabled)
            return float.NaN;

        float resolvedGain = track != null
            ? NormalizationGainResolver.Resolve(track, normConfig)
            : float.NaN;

        if (float.IsNaN(resolvedGain)
            && cacheEntry?.IntegratedLufs is float cacheIntegratedLufs
            && float.IsFinite(cacheIntegratedLufs))
        {
            resolvedGain = NormalizationGainResolver.ComputeGainFromIntegratedLufs(
                cacheIntegratedLufs,
                normConfig);
        }

        return resolvedGain;
    }

    #endregion

    #region Loudness Persistence & Sync

    /// <summary>
    /// Persists the resolved integrated loudness into the runtime model, AudioCache, and DB persistence queue.
    /// </summary>
    private void CommitIntegratedLufs(string trackId, float integratedLufs, LoudnessSource source)
    {
        if (string.IsNullOrEmpty(trackId) || !float.IsFinite(integratedLufs))
            return;

        var canonical = _library.GetTrack(trackId);
        canonical?.SetIntegratedLufs(integratedLufs, source);

        var registryTrack = _trackRegistry.TryGet(trackId);
        if (registryTrack != null && !ReferenceEquals(registryTrack, canonical))
            registryTrack.SetIntegratedLufs(integratedLufs, source);

        var current = CurrentTrack;
        if (current != null
            && current.Id == trackId
            && !ReferenceEquals(current, canonical)
            && !ReferenceEquals(current, registryTrack))
        {
            current.SetIntegratedLufs(integratedLufs, source);
        }

        AudioSourceFactory.GlobalCache.TryUpdateIntegratedLufs(trackId, integratedLufs, source);

        _pendingNormalizationWrites.Enqueue((trackId, integratedLufs, source));
        NotifyPersistenceNeeded();
    }

    private void DrainPendingNormalizationWrites(
        Dictionary<string, (float IntegratedLufs, LoudnessSource Source)> target)
    {
        target.Clear();
        while (_pendingNormalizationWrites.TryDequeue(out var pending))
            target[pending.TrackId] = (pending.IntegratedLufs, pending.Source);
    }

    private Task PersistNormalizationEntryAsync(
        string trackId,
        (float IntegratedLufs, LoudnessSource Source) data,
        CancellationToken ct)
    {
        var track = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
        if (track != null)
        {
            track.SetIntegratedLufs(data.IntegratedLufs, data.Source);
            return _library.AddOrUpdateTrackAsync(track, ct);
        }

        return _library.SaveTrackNormalizationMetadataAsync(
            trackId,
            data.IntegratedLufs,
            (int)data.Source,
            ct);
    }

    /// <summary>
    /// Asynchronously saves pending integrated loudness entries to the DB.
    /// </summary>
    private async Task FlushPendingNormalizationWritesAsync(CancellationToken ct)
    {
        if (_pendingNormalizationWrites.IsEmpty) return;

        var localBatch = new Dictionary<string, (float IntegratedLufs, LoudnessSource Source)>(StringComparer.Ordinal);
        DrainPendingNormalizationWrites(localBatch);

        if (localBatch.Count == 0) return;

        foreach (var (trackId, data) in localBatch)
        {
            try
            {
                await PersistNormalizationEntryAsync(trackId, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[AudioEngine] Failed to persist normalization metadata for {trackId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Synchronously saves all pending integrated loudness entries to the database.
    /// </summary>
    private void FlushPendingNormalizationWritesSync()
    {
        if (_pendingNormalizationWrites.IsEmpty) return;

        lock (_normalizationBatch)
        {
            DrainPendingNormalizationWrites(_normalizationBatch);
            if (_normalizationBatch.Count == 0) return;

            foreach (var (trackId, data) in _normalizationBatch)
            {
                try
                {
                    PersistNormalizationEntryAsync(trackId, data, CancellationToken.None)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn(
                        $"[AudioEngine] Failed to sync persist normalization metadata for {trackId}: {ex.Message}");
                }
            }
        }
    }

    #endregion

    #region Dynamic Gain & Equalizer Application

    /// <summary>
    /// Updates the normalization gain in the running pipeline upon receiving a more accurate LUFS value.
    /// </summary>
    private void UpdateRunningPipelineGain(string trackId, float integratedLufs)
    {
        if (!float.IsFinite(integratedLufs)) return;

        var pipeline = _player.GetActivePipeline();
        if (pipeline == null) return;
        if (!string.Equals(pipeline.StreamInfo.TrackId, trackId, StringComparison.Ordinal)) return;

        var audioSettings = _library.Settings.Audio;
        if (!audioSettings.NormalizationEnabled) return;

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode);

        float gain = NormalizationGainResolver.ComputeGainFromIntegratedLufs(integratedLufs, normConfig);
        if (float.IsNaN(gain)) return;

        if (pipeline.Analyzer.IsGainLocked && MathF.Abs(pipeline.GetLockedNormalizationGain() - gain) < 0.0005f)
            return;

        pipeline.Analyzer.LockResolvedGain(gain);
        Log.Info(
            $"[AudioEngine] Normalization gain updated from YouTube LUFS: {gain:F4}x (lufs={integratedLufs:F2}) for {trackId}");
    }

    /// <summary>
    /// Propagates the latest normalization and equalizer settings to the active pipeline.
    /// </summary>
    private readonly Lock _eqPersistenceLock = new();
    private Timer? _eqSaveTimer;
    private bool _pendingEqGlobalSave;
    private TrackInfo? _pendingEqTrack;

    /// <summary>
    /// Propagates the latest normalization and equalizer settings to the active pipeline.
    /// </summary>
    private void ApplyNormalizationToPipeline()
    {
        var pipeline = _player.GetActivePipeline();
        if (pipeline != null)
        {
            var audioSettings = _library.Settings.Audio;
            var normConfig = new NormalizationConfig(
                audioSettings.NormalizationEnabled,
                audioSettings.NormalizationTargetLufs,
                audioSettings.NormalizationMaxGain,
                audioSettings.NormalizationMode);

            var track = CurrentTrack;
            float resolvedGain = ResolveTrackNormalizationGain(track, track?.Id, normConfig, out _);

            pipeline.Analyzer.Configure(normConfig);

            if (normConfig.Enabled && !float.IsNaN(resolvedGain))
                pipeline.Analyzer.LockResolvedGain(resolvedGain);

            ApplyEqualizerToPipelineCore(pipeline, track?.Id, snapImmediately: false);
        }

        NotifyAudioSettingsChanged();
    }

    /// <summary>
    /// Broadcasts audio setting changes (such as VolumeBoostEnabled or VolumeCurve) to UI subscribers.
    /// </summary>
    public void NotifyAudioSettingsChanged()
    {
        RaiseOnUI(() => OnAudioSettingsChanged?.Invoke());
    }

    /// <summary>
    /// Applies the current equalizer configuration (taking into account ForceGlobal toggle and track preset) to the active pipeline
    /// and notifies all synchronized UI views.
    /// </summary>
    private void UpdateEqualizerSettings()
    {
        var pipeline = _player.GetActivePipeline();
        ApplyEqualizerToPipelineCore(pipeline, CurrentTrack?.Id, snapImmediately: false);
        RaiseOnUI(() => OnEqualizerChanged?.Invoke());
    }

    /// <summary>
    /// Returns true if the current track has an active per-track equalizer profile and ForceGlobalEqualizer is disabled.
    /// </summary>
    public bool IsTrackEqualizerActive
    {
        get
        {
            var track = GetCanonicalCurrentTrack();
            return track != null
                && EqualizerPresets.IsKnownPresetId(_library.Settings.Audio, track.EqualizerPresetId);
        }
    }

    private TrackInfo? GetCanonicalCurrentTrack()
    {
        var current = CurrentTrack;
        if (current == null) return null;
        return _trackRegistry.TryGet(current.Id)
            ?? _library.GetTrack(current.Id)
            ?? current;
    }

    private void SyncTrackPresetInMemory(TrackInfo track, string? presetId)
    {
        track.EqualizerPresetId = presetId;
        if (CurrentTrack != null && string.Equals(CurrentTrack.Id, track.Id, StringComparison.Ordinal))
            CurrentTrack.EqualizerPresetId = presetId;

        var reg = _trackRegistry.TryGet(track.Id);
        if (reg != null)
            reg.EqualizerPresetId = presetId;

        var lib = _library.GetTrack(track.Id);
        if (lib != null)
            lib.EqualizerPresetId = presetId;
    }

    /// <summary>
    /// Sets whether the equalizer is enabled globally and updates the DSP pipeline and UI.
    /// </summary>
    public void SetEqualizerEnabled(bool enabled)
    {
        if (_library.Settings.Audio.EqualizerEnabled == enabled
            && !_library.Settings.Audio.ForceGlobalEqualizer)
        {
            return;
        }

        FlushPendingEqualizerSave();
        _library.UpdateSettings(s =>
        {
            s.Audio.EqualizerEnabled = enabled;
            s.Audio.ForceGlobalEqualizer = false;
        });
        UpdateEqualizerSettings();
    }

    /// <summary>
    /// Sets whether the equalizer UI uses the simplified 5-band macro mode or 10-band ISO mode.
    /// </summary>
    public void SetSimple5BandMode(bool useSimple5Band)
    {
        if (_library.Settings.Audio.UseSimple5BandMode == useSimple5Band)
            return;

        FlushPendingEqualizerSave();
        _library.UpdateSettings(s => s.Audio.UseSimple5BandMode = useSimple5Band);
        RaiseOnUI(() => OnEqualizerChanged?.Invoke());
    }

    /// <summary>
    /// Sets the manual equalizer preamp gain in dB [-12..+12] with immediate DSP application and debounced settings persistence.
    /// </summary>
    public void SetEqualizerPreampDb(float preampDb)
    {
        float clamped = Math.Clamp(preampDb, -12f, 12f);
        var audioSettings = _library.Settings.Audio;

        lock (_eqPersistenceLock)
        {
            if (MathF.Abs(audioSettings.EqualizerPreampDb - clamped) < 1e-4f)
                return;

            audioSettings.EqualizerPreampDb = clamped;
            _pendingEqGlobalSave = true;

            if (Volatile.Read(ref _disposeState) == 0)
            {
                if (_eqSaveTimer == null)
                {
                    _eqSaveTimer = new Timer(static state =>
                    {
                        ((AudioEngine)state!).FlushPendingEqualizerSave();
                    }, this, 300, Timeout.Infinite);
                }
                else
                {
                    _eqSaveTimer.Change(300, Timeout.Infinite);
                }
            }
        }

        var pipeline = _player.GetActivePipeline();
        ApplyEqualizerToPipelineCore(pipeline, CurrentTrack?.Id, snapImmediately: false);
    }

    /// <summary>
    /// Toggles per-track equalizer mode for the current track in a single click:
    /// - When inactive: immediately assigns &amp; saves the current effective preset/curve to the track.
    /// - When active: detaches the per-track preset (<c>null</c>) so the track reverts to the global equalizer.
    /// </summary>
    public void ToggleTrackEqualizerMode()
    {
        var track = GetCanonicalCurrentTrack();
        if (track == null) return;

        FlushPendingEqualizerSave();
        var audioSettings = _library.Settings.Audio;

        if (IsTrackEqualizerActive)
        {
            if (!audioSettings.EqualizerEnabled || audioSettings.ForceGlobalEqualizer)
            {
                _library.UpdateSettings(s =>
                {
                    s.Audio.EqualizerEnabled = true;
                    s.Audio.ForceGlobalEqualizer = false;
                });
                UpdateEqualizerSettings();
                return;
            }

            SyncTrackPresetInMemory(track, null);
            _ = _library.SetTrackEqualizerPresetAsync(track, null);
            UpdateEqualizerSettings();
            return;
        }

        Span<float> currentBands = stackalloc float[EqualizerProcessor.BandCount];
        string effectiveId = EqualizerPresets.ResolveEffectiveBands(audioSettings, null, currentBands);

        string targetTrackPreset = string.Equals(effectiveId, EqualizerPresets.CustomId, StringComparison.Ordinal)
            ? EqualizerPresets.EncodeCustomTrackBands(currentBands)
            : effectiveId;

        if (audioSettings.ForceGlobalEqualizer || !audioSettings.EqualizerEnabled)
        {
            _library.UpdateSettings(s =>
            {
                s.Audio.ForceGlobalEqualizer = false;
                s.Audio.EqualizerEnabled = true;
            });
        }

        SyncTrackPresetInMemory(track, targetTrackPreset);
        _ = _library.SetTrackEqualizerPresetAsync(track, targetTrackPreset);
        UpdateEqualizerSettings();
    }

    /// <summary>
    /// Selects an equalizer preset by ID for the currently active scope (per-track if Track EQ is active, otherwise global).
    /// </summary>
    public void SelectEqualizerPreset(string presetId)
    {
        if (string.IsNullOrEmpty(presetId)) return;

        FlushPendingEqualizerSave();
        var audioSettings = _library.Settings.Audio;
        bool enableEq = !audioSettings.EqualizerEnabled
                     && !string.Equals(presetId, EqualizerPresets.FlatId, StringComparison.Ordinal);

        if (IsTrackEqualizerActive && GetCanonicalCurrentTrack() is { } track)
        {
            if (enableEq)
                _library.UpdateSettings(s => s.Audio.EqualizerEnabled = true);

            SyncTrackPresetInMemory(track, presetId);
            _ = _library.SetTrackEqualizerPresetAsync(track, presetId);
        }
        else
        {
            _library.UpdateSettings(s =>
            {
                s.Audio.GlobalEqualizerPresetId = presetId;
                if (enableEq)
                    s.Audio.EqualizerEnabled = true;
            });
        }

        UpdateEqualizerSettings();
    }

    /// <summary>
    /// Resets the equalizer bands to Flat and preamp to 0 dB for the active scope.
    /// </summary>
    public void ResetEqualizer()
    {
        FlushPendingEqualizerSave();
        bool isTrackScope = IsTrackEqualizerActive;

        _library.UpdateSettings(s =>
        {
            lock (_eqPersistenceLock)
            {
                s.Audio.EqualizerPreampDb = 0f;
                if (!isTrackScope && s.Audio.CustomEqualizerBands != null)
                    Array.Clear(s.Audio.CustomEqualizerBands);
            }
        });

        SelectEqualizerPreset(EqualizerPresets.FlatId);
    }

    private readonly float[] _liveTrackCustomBands = new float[EqualizerProcessor.BandCount];

    /// <summary>
    /// Applies a custom 10-band equalizer curve from the UI with 0 ms DSP latency and 0 B hot-path allocations,
    /// isolating per-track custom curves from global custom bands and debouncing SQLite writes.
    /// </summary>
    public void ApplyCustomEqualizerBands(ReadOnlySpan<float> newBands10)
    {
        if (newBands10.Length < EqualizerProcessor.BandCount)
            return;

        var audioSettings = _library.Settings.Audio;
        Span<float> clampedBands = stackalloc float[EqualizerProcessor.BandCount];
        for (int i = 0; i < EqualizerProcessor.BandCount; i++)
        {
            clampedBands[i] = Math.Clamp(
                newBands10[i],
                EqualizerProcessor.MinGainDb,
                EqualizerProcessor.MaxGainDb);
        }

        bool notifyUiHeaderImmediately;
        float preampDbSnapshot;

        lock (_eqPersistenceLock)
        {
            bool wasEnabled = audioSettings.EqualizerEnabled;
            audioSettings.EqualizerEnabled = true;
            notifyUiHeaderImmediately = !wasEnabled;
            preampDbSnapshot = audioSettings.EqualizerPreampDb;

            if (IsTrackEqualizerActive && GetCanonicalCurrentTrack() is { } track)
            {
                clampedBands.CopyTo(_liveTrackCustomBands);
                _pendingEqTrack = track;

                string encoded = EqualizerPresets.EncodeCustomTrackBands(clampedBands);
                if (!string.Equals(track.EqualizerPresetId, encoded, StringComparison.Ordinal))
                {
                    if (!EqualizerPresets.IsCustomPresetId(track.EqualizerPresetId)
                        || string.Equals(encoded, EqualizerPresets.FlatId, StringComparison.Ordinal))
                    {
                        notifyUiHeaderImmediately = true;
                    }

                    SyncTrackPresetInMemory(track, encoded);
                }

                if (!wasEnabled)
                    _pendingEqGlobalSave = true;
            }
            else
            {
                if (!string.Equals(
                        audioSettings.GlobalEqualizerPresetId,
                        EqualizerPresets.CustomId,
                        StringComparison.Ordinal))
                {
                    notifyUiHeaderImmediately = true;
                }

                if (audioSettings.CustomEqualizerBands == null
                    || audioSettings.CustomEqualizerBands.Length != EqualizerProcessor.BandCount)
                {
                    audioSettings.CustomEqualizerBands = new float[EqualizerProcessor.BandCount];
                }

                clampedBands.CopyTo(audioSettings.CustomEqualizerBands);
                audioSettings.GlobalEqualizerPresetId = EqualizerPresets.CustomId;
                _pendingEqGlobalSave = true;
            }

            if (Volatile.Read(ref _disposeState) == 0)
            {
                if (_eqSaveTimer == null)
                {
                    _eqSaveTimer = new Timer(static state =>
                    {
                        ((AudioEngine)state!).FlushPendingEqualizerSave();
                    }, this, 300, Timeout.Infinite);
                }
                else
                {
                    _eqSaveTimer.Change(300, Timeout.Infinite);
                }
            }
        }

        var pipeline = _player.GetActivePipeline();
        pipeline?.Equalizer.Configure(
            enabled: true,
            clampedBands,
            preampDbSnapshot,
            autoPreamp: true);

        if (notifyUiHeaderImmediately)
        {
            RaiseOnUI(() => OnEqualizerChanged?.Invoke());
        }
    }

    /// <summary>
    /// Immediately flushes any pending debounced equalizer settings or per-track preset changes to persistent storage.
    /// </summary>
    public void FlushPendingEqualizerSave()
    {
        bool saveGlobal;
        TrackInfo? trackToSave;
        string? encodedTrackPreset;
        float[]? globalBandsSnapshot;
        bool enabledSnapshot;
        string globalPresetSnapshot;
        float preampSnapshot;

        lock (_eqPersistenceLock)
        {
            _eqSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            saveGlobal = _pendingEqGlobalSave;
            trackToSave = _pendingEqTrack;

            if (trackToSave != null)
            {
                encodedTrackPreset = EqualizerPresets.EncodeCustomTrackBands(_liveTrackCustomBands);
                SyncTrackPresetInMemory(trackToSave, encodedTrackPreset);
            }
            else
            {
                encodedTrackPreset = null;
            }

            if (saveGlobal)
            {
                var currentAudio = _library.Settings.Audio;
                globalBandsSnapshot = new float[EqualizerProcessor.BandCount];
                if (currentAudio.CustomEqualizerBands != null)
                {
                    int len = Math.Min(currentAudio.CustomEqualizerBands.Length, globalBandsSnapshot.Length);
                    currentAudio.CustomEqualizerBands.AsSpan(0, len).CopyTo(globalBandsSnapshot);
                }

                enabledSnapshot = currentAudio.EqualizerEnabled;
                globalPresetSnapshot = currentAudio.GlobalEqualizerPresetId;
                preampSnapshot = currentAudio.EqualizerPreampDb;
            }
            else
            {
                globalBandsSnapshot = null;
                enabledSnapshot = false;
                globalPresetSnapshot = EqualizerPresets.FlatId;
                preampSnapshot = 0f;
            }

            _pendingEqGlobalSave = false;
            _pendingEqTrack = null;
        }

        if (saveGlobal && globalBandsSnapshot != null)
        {
            _library.UpdateSettings(s =>
            {
                s.Audio.EqualizerEnabled = enabledSnapshot;
                s.Audio.CustomEqualizerBands = globalBandsSnapshot;
                s.Audio.GlobalEqualizerPresetId = globalPresetSnapshot;
                s.Audio.EqualizerPreampDb = preampSnapshot;
            });
        }

        if (trackToSave != null)
        {
            _ = _library.SetTrackEqualizerPresetAsync(trackToSave, encodedTrackPreset);
        }

        if (saveGlobal || trackToSave != null)
        {
            RaiseOnUI(() => OnEqualizerChanged?.Invoke());
        }
    }

    /// <summary>
    /// Saves the current 10-band curve as a named user preset and activates it in the current scope (track or global).
    /// </summary>
    public string? SaveUserEqualizerPreset(string? rawName, ReadOnlySpan<float> bands10)
    {
        string name = rawName?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return null;

        FlushPendingEqualizerSave();

        string id = "user_" + Guid.NewGuid().ToString("N")[..8];
        var bandsCopy = new float[EqualizerProcessor.BandCount];
        EqualizerPresets.ResolveEffectiveBands(
            _library.Settings.Audio,
            GetCanonicalCurrentTrack(),
            bandsCopy);

        if (!EqualizerPresets.HasNonZeroBands(bandsCopy) && bands10.Length >= EqualizerProcessor.BandCount)
        {
            for (int i = 0; i < EqualizerProcessor.BandCount; i++)
            {
                bandsCopy[i] = Math.Clamp(
                    bands10[i],
                    EqualizerProcessor.MinGainDb,
                    EqualizerProcessor.MaxGainDb);
            }
        }

        bool assignToTrack = IsTrackEqualizerActive && GetCanonicalCurrentTrack() != null;

        _library.UpdateSettings(s =>
        {
            s.Audio.UserEqualizerPresets.Add(new EqualizerPreset
            {
                Id = id,
                Name = name,
                IsBuiltIn = false,
                Bands = bandsCopy
            });
            s.Audio.EqualizerEnabled = true;
            if (!assignToTrack)
                s.Audio.GlobalEqualizerPresetId = id;
        });

        if (assignToTrack && GetCanonicalCurrentTrack() is { } track)
        {
            SyncTrackPresetInMemory(track, id);
            _ = _library.SetTrackEqualizerPresetAsync(track, id);
        }

        UpdateEqualizerSettings();
        return id;
    }

    /// <summary>
    /// Renames an existing custom user equalizer preset and notifies all UI subscribers.
    /// </summary>
    public bool RenameUserEqualizerPreset(string presetId, string? rawNewName)
    {
        string newName = rawNewName?.Trim() ?? "";
        if (string.IsNullOrEmpty(presetId) || string.IsNullOrEmpty(newName))
            return false;

        FlushPendingEqualizerSave();
        bool changed = false;

        _library.UpdateSettings(s =>
        {
            var list = s.Audio.UserEqualizerPresets;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Id, presetId, StringComparison.Ordinal))
                {
                    if (!string.Equals(list[i].Name, newName, StringComparison.Ordinal))
                    {
                        list[i].Name = newName;
                        changed = true;
                    }
                    break;
                }
            }
        });

        if (changed)
            RaiseOnUI(() => OnEqualizerChanged?.Invoke());

        return changed;
    }

    /// <summary>
    /// Deletes a custom user equalizer preset, reverting active references to Flat.
    /// </summary>
    public void DeleteUserEqualizerPreset(string presetId)
    {
        if (string.IsNullOrEmpty(presetId) || EqualizerPresets.IsBuiltInPreset(presetId))
            return;

        FlushPendingEqualizerSave();

        _library.UpdateSettings(s =>
        {
            s.Audio.UserEqualizerPresets.RemoveAll(p => string.Equals(p.Id, presetId, StringComparison.Ordinal));
            if (string.Equals(s.Audio.GlobalEqualizerPresetId, presetId, StringComparison.Ordinal))
                s.Audio.GlobalEqualizerPresetId = EqualizerPresets.FlatId;
        });

        if (GetCanonicalCurrentTrack() is { } track
            && string.Equals(track.EqualizerPresetId, presetId, StringComparison.Ordinal))
        {
            SyncTrackPresetInMemory(track, null);
            _ = _library.SetTrackEqualizerPresetAsync(track, null);
        }

        UpdateEqualizerSettings();
    }

    private string? _lastLoggedEqPresetId;
    private bool _lastLoggedEqEnabled;
    private void ApplyEqualizerToPipelineCore(AudioPipeline? pipeline, string? trackId, bool snapImmediately)
    {
        var audioSettings = _library.Settings.Audio;

        TrackInfo? track = null;
        if (!string.IsNullOrEmpty(trackId))
        {
            track = _trackRegistry.TryGet(trackId)
                 ?? _library.GetTrack(trackId)
                 ?? (CurrentTrack?.Id == trackId ? CurrentTrack : null);
        }

        Span<float> bands = stackalloc float[EqualizerProcessor.BandCount];
        string effectivePresetId;
        bool eqEnabled;
        float preampDb;

        lock (_eqPersistenceLock)
        {
            if (audioSettings.ForceGlobalEqualizer)
                audioSettings.ForceGlobalEqualizer = false;

            eqEnabled = audioSettings.EqualizerEnabled;
            preampDb = audioSettings.EqualizerPreampDb;
            effectivePresetId = EqualizerPresets.ResolveEffectiveBands(audioSettings, track, bands);
        }

        if (pipeline != null)
        {
            pipeline.Equalizer.Configure(
                eqEnabled,
                bands,
                preampDb,
                autoPreamp: true);
        }

        if (snapImmediately)
        {
            pipeline?.Equalizer.SnapToTarget();
            _lastLoggedEqPresetId = effectivePresetId;
            _lastLoggedEqEnabled = eqEnabled;
            Log.Debug(
                $"[AudioEngine] Equalizer initial config for '{trackId}': enabled={eqEnabled}, " +
                $"preset='{effectivePresetId}', preamp={preampDb:+0.0;-0.0;0.0}dB");
        }
        else if (_lastLoggedEqEnabled != eqEnabled
              || !string.Equals(_lastLoggedEqPresetId, effectivePresetId, StringComparison.Ordinal))
        {
            _lastLoggedEqEnabled = eqEnabled;
            _lastLoggedEqPresetId = effectivePresetId;
            Log.Info(
                $"[AudioEngine] Equalizer updated: enabled={eqEnabled}, " +
                $"preset='{effectivePresetId}' (trackPreset='{track?.EqualizerPresetId ?? "none"}', " +
                $"preamp={preampDb:+0.0;-0.0;0.0}dB)");
        }
    }

    #endregion
}
