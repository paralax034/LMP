using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace LMP.UI.Controls;

/// <summary>
/// Border decorator driven by left-to-right traveling hydrodynamic wave packets ("audio history stream")
/// with immediate left-edge entry (0..80 ms audiovisual binding window), zero-mean bipolar bass/kick wavelets
/// (prevents DC saturation on bass-heavy tracks), adaptive wave-density headroom + smooth tanh ceiling,
/// non-evicting smooth birth/exit envelopes (zero abrupt disappearances), and optional reactive bloom.
/// Runs 60 FPS inside <see cref="CompositionCustomVisualHandler"/> on the render thread with 0 B heap allocations.
/// </summary>
public sealed class AudioWaveBorder : Decorator
{
    #region Constants

    private const double DefaultBorderThickness = 1.35;
    private const double SelectedBorderThickness = 2.4;
    private const float InsetMarginX = 1.5f;
    private const float InsetMarginY = 3.8f;

    private const double BasePulseSpeed = 1.4;
    private const double MinFrameIntervalSeconds = 0.0155;
    private const double MaxFrameDeltaSeconds = 0.050;

    internal const int RailSamples = 64;
    internal const int MaxWavePackets = 24;
    internal const float MaxSafeDisplacement = 3.45f;
    private const float PacketAttackSeconds = 0.050f;
    private const float CornerBlendMargin = 0.07f;
    private const float DisplacementZeroEpsilon = 1e-4f;
    private const float BezierCircleConstant = 0.5522847498f;

    private static readonly object MsgStart = new();
    private static readonly object MsgStop = new();
    private static readonly object MsgResumeFromSilence = new();
    private static readonly object MsgSyncState = new();

    #endregion

    #region Wave Packet Types

    internal enum WavePacketKind : byte
    {
        KickBullet = 0,
        BassSwell = 1,
        MidRipple = 2,
        HighNeedle = 3
    }

    internal struct WavePacket
    {
        public float Position;
        public float HalfWidth;
        public float Speed;
        public float Amplitude;
        public float Frequency;
        public float Phase;
        public float Age;
        public float MaxAge;
        public float PauseFade;
        public WavePacketKind Kind;
        public bool IsActive;
    }

    private struct ActiveWavePacket
    {
        public float Position;
        public float InvHalfWidth;
        public float EffectiveAmp;
        public float Frequency;
        public float Phase;
        public WavePacketKind Kind;
    }

    #endregion

    #region Wave State & Composition Handler

    private struct WaveStateSnapshot
    {
        public bool IsActive;
        public bool IsDraining;
        public bool IsPlaying;
        public bool IsSelected;
        public bool UseWave;
        public bool ReactiveGlow;
        public TrackAnimationSpeed Speed;
        public float Thickness;
        public float CornerRadius;
        public double VolumeFactor;
        public string? TrackId;
        public SKColor Color;
        public ulong Seed;
        public bool SeedChanged;
        public bool CanAnimate;
    }

    private sealed class WaveBorderHandler : CompositionCustomVisualHandler
    {
        [ThreadStatic] private static SKPath? t_path;
        [ThreadStatic] private static SKPaint? t_paint;

        private readonly AudioWaveBorder _owner;
        private readonly WavePacket[] _packets = new WavePacket[MaxWavePackets];

        private WaveStateSnapshot _state;
        private AudioReactiveSignalState _signal;
        private FastXorShift64 _rng;
        private bool _rngInitialized;

        // Perceptual envelopes & 2nd-order spring stroke/glow physics
        private double _macroEnergy;
        private double _vocalMode;
        private double _harmonicMode;
        private double _percFlash;
        private double _centroid = 0.40;
        private double _vocalPhase;
        private double _cooldownVocal;
        private double _springPos;
        private double _springVel;
        private double _pulsePhase;
        private float _drainOpacity = 1f;
        private byte _lastPulseAlpha;
        private bool _running;
        private bool _frameScheduled;
        private bool _wasIdleFrame;
        private TimeSpan? _lastFrameTime;

        public WaveBorderHandler(AudioWaveBorder owner)
        {
            _owner = owner;
        }

        public override Rect GetRenderBounds()
        {
            double pad = MaxSafeDisplacement + (_state.Thickness * 3.5) + 3.0;
            return new Rect(-pad, -pad, EffectiveSize.X + (2.0 * pad), EffectiveSize.Y + (2.0 * pad));
        }

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, MsgStop))
            {
                _running = false;
                return;
            }

            if (ReferenceEquals(message, MsgResumeFromSilence))
            {
                if (_state.CanAnimate && _state.IsActive && _state.IsPlaying && !_running)
                {
                    _running = true;
                    _lastFrameTime = null;
                    ScheduleNextFrame(forceRegister: true);
                }
                return;
            }

            if (ReferenceEquals(message, MsgStart))
            {
                SyncFromOwner();
                if (_state.IsActive)
                    _drainOpacity = 1f;

                if (_state.CanAnimate && _state.IsActive && _state.IsPlaying && !HasActivePackets(_packets))
                    TryRestoreSharedWaveState(_state.TrackId, _packets, ref _signal, ref _pulsePhase, ref _macroEnergy, ref _vocalMode);

                Invalidate();
                if (_state.CanAnimate && _state.IsActive && _state.IsPlaying && !_running)
                {
                    _running = true;
                    _lastFrameTime = null;
                    ScheduleNextFrame(forceRegister: true);
                }
                return;
            }

            if (ReferenceEquals(message, MsgSyncState))
            {
                SyncFromOwner();

                if (_state.IsActive)
                    _drainOpacity = 1f;

                if (!_state.IsActive && !_state.IsDraining)
                {
                    _running = false;
                    ResetState();
                    Invalidate();
                    return;
                }

                if (!_state.CanAnimate)
                {
                    _running = false;
                    ResetState();
                    Invalidate();
                    if (_state.IsDraining)
                        _owner.PostDrainComplete();
                    return;
                }

                if (!_state.UseWave)
                {
                    ClearPackets();
                    _lastPulseAlpha = 0;
                }

                if (_state.IsActive && _state.IsPlaying && !HasActivePackets(_packets))
                    TryRestoreSharedWaveState(_state.TrackId, _packets, ref _signal, ref _pulsePhase, ref _macroEnergy, ref _vocalMode);

                if (!_state.IsActive && _state.IsDraining && _state.UseWave && !HasActivePackets(_packets) && !_signal.HasEnergy(0.005))
                {
                    _running = false;
                    ResetState();
                    Invalidate();
                    _owner.PostDrainComplete();
                    return;
                }

                Invalidate();

                bool shouldRun = (_state.IsActive && _state.IsPlaying)
                              || HasResidualEnergy()
                              || (!_state.IsActive && _state.IsDraining && _drainOpacity > 0.004f);

                if (shouldRun)
                {
                    if (!_running)
                    {
                        _running = true;
                        _lastFrameTime = null;
                        ScheduleNextFrame(forceRegister: true);
                    }
                }
                else
                {
                    _running = false;
                }
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            _frameScheduled = false;

            if (!_running || !_state.CanAnimate || (!_state.IsActive && !_state.IsDraining))
            {
                _running = false;
                return;
            }

            var now = CompositionNow;
            if (!_lastFrameTime.HasValue)
                _lastFrameTime = now - TimeSpan.FromMilliseconds(16.6);

            double dt = (now - _lastFrameTime.Value).TotalSeconds;
            if (dt < MinFrameIntervalSeconds)
            {
                ScheduleNextFrame();
                return;
            }

            _lastFrameTime = now;
            if (dt > MaxFrameDeltaSeconds) dt = MaxFrameDeltaSeconds;

            var (needsRedraw, continueAnim) = TickVisualizerFrame(dt, _state.IsActive && _state.IsPlaying);
            if (needsRedraw)
                Invalidate();

            if (continueAnim)
            {
                ScheduleNextFrame();
                return;
            }

            _running = false;
            if (_state.IsDraining && !_state.IsActive)
            {
                ResetState();
                Invalidate();
                _owner.PostDrainComplete();
            }
            else if (!_state.IsPlaying)
            {
                ResetState();
                Invalidate();
            }
            else
            {
                _owner._silenceMonitor?.PostSilenceEntered();
            }
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (!_state.IsActive && !_state.IsDraining) return;

            double cw = EffectiveSize.X;
            double ch = EffectiveSize.Y;
            if (cw < 32.0 || ch < 16.0) return;

            float width = (float)cw - (2f * InsetMarginX);
            float height = (float)ch - (2f * InsetMarginY);
            float radius = Math.Clamp(_state.CornerRadius, 2f, MathF.Min(width * 0.5f, height * 0.5f));
            if (width - (2f * radius) <= 0f || height - (2f * radius) <= 0f) return;

            var leaseFeature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();

            bool reactiveGlow = _state.ReactiveGlow;
            float springKick = MathF.Max(0f, (float)_springPos);
            float macro = (float)Math.Clamp(_macroEnergy, 0.0, 1.4);
            float vocal = (float)Math.Clamp(_vocalMode, 0.0, 1.0);
            float percFlash = (float)Math.Clamp(_percFlash, 0.0, 1.0);

            byte baseAlpha = _state.UseWave
                ? _state.Color.Alpha
                : ComputePulseAlpha(_state.Color.Alpha, _state.IsSelected, _signal.Beat + (0.25 * springKick), _pulsePhase);

            byte alpha = (byte)Math.Clamp((int)Math.Round(baseAlpha * lease.CurrentOpacity * _drainOpacity), 0, 255);
            if (alpha == 0) return;

            byte cr = _state.Color.Red;
            byte cg = _state.Color.Green;
            byte cb = _state.Color.Blue;
            float strokeThickness = _state.Thickness;
            byte glowAlpha = 0;

            if (reactiveGlow)
            {
                float brightLift = Math.Clamp((percFlash * 0.36f) + (springKick * 0.26f) + (vocal * 0.14f), 0f, 0.58f);
                cr = (byte)Math.Clamp((int)MathF.Round(cr + ((255f - cr) * brightLift)), 0, 255);
                cg = (byte)Math.Clamp((int)MathF.Round(cg + ((255f - cg) * brightLift)), 0, 255);
                cb = (byte)Math.Clamp((int)MathF.Round(cb + ((255f - cb) * brightLift)), 0, 255);

                strokeThickness = Math.Clamp(
                    _state.Thickness * (0.92f + (0.24f * macro) + (0.40f * springKick) + (0.12f * vocal)),
                    1.0f,
                    2.7f);

                float glowStrength = Math.Clamp((macro * 0.35f) + (springKick * 0.52f) + (vocal * 0.35f) + (percFlash * 0.38f), 0f, 1.0f);
                glowAlpha = (byte)Math.Clamp((int)MathF.Round(alpha * 0.32f * glowStrength), 0, 110);
            }

            var canvas = lease.SkCanvas;
            var paint = t_paint ??= new SKPaint();
            paint.Reset();
            paint.IsAntialias = true;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;

            float left = InsetMarginX;
            float top = InsetMarginY;
            float right = InsetMarginX + width;
            float bottom = InsetMarginY + height;

            bool hasWaveEnergy = HasActivePackets(_packets) || vocal >= 0.008f || _harmonicMode >= 0.01;
            if (!_state.UseWave || !hasWaveEnergy)
            {
                var rect = new SKRect(left, top, right, bottom);
                if (glowAlpha > 3)
                {
                    paint.StrokeWidth = strokeThickness * 2.1f;
                    paint.Color = new SKColor(cr, cg, cb, glowAlpha);
                    canvas.DrawRoundRect(rect, radius, radius, paint);
                }

                paint.StrokeWidth = strokeThickness;
                paint.Color = new SKColor(cr, cg, cb, alpha);
                canvas.DrawRoundRect(rect, radius, radius, paint);
                paint.Reset();
                return;
            }

            float horizLen = width - (2f * radius);
            float invNMinusOne = 1f / (RailSamples - 1);
            float k = radius * BezierCircleConstant;

            // Single stack-allocated displacement evaluation shared symmetrically by top & bottom rails (0 B heap alloc)
            Span<float> disp = stackalloc float[RailSamples];
            float vocalAmp = vocal * (0.72f + (0.38f * macro));
            float harmAmp = (float)_harmonicMode * 0.26f;
            EvaluateRailDisplacement(_packets, disp, vocalAmp, harmAmp, (float)_vocalPhase, (float)_centroid);

            var path = t_path ??= new SKPath();
            path.Rewind();

            float startX = left + radius;
            path.MoveTo(startX, top);

            float prevX = startX;
            float prevY = top;
            for (int i = 1; i < RailSamples - 1; i++)
            {
                float curX = startX + (i * invNMinusOne * horizLen);
                float curY = top - disp[i];
                float midX = (prevX + curX) * 0.5f;
                float midY = (prevY + curY) * 0.5f;
                if (i == 1)
                    path.LineTo(midX, midY);
                else
                    path.QuadTo(prevX, prevY, midX, midY);

                prevX = curX;
                prevY = curY;
            }

            float rightInnerX = right - radius;
            path.QuadTo(prevX, prevY, rightInnerX, top);

            path.CubicTo(rightInnerX + k, top, right, top + radius - k, right, top + radius);
            path.LineTo(right, bottom - radius);
            path.CubicTo(right, bottom - radius + k, rightInnerX + k, bottom, rightInnerX, bottom);

            prevX = rightInnerX;
            prevY = bottom;
            for (int i = 1; i < RailSamples - 1; i++)
            {
                int idx = RailSamples - 1 - i;
                float curX = startX + (idx * invNMinusOne * horizLen);
                float curY = bottom + disp[idx];
                float midX = (prevX + curX) * 0.5f;
                float midY = (prevY + curY) * 0.5f;
                if (i == 1)
                    path.LineTo(midX, midY);
                else
                    path.QuadTo(prevX, prevY, midX, midY);

                prevX = curX;
                prevY = curY;
            }

            path.QuadTo(prevX, prevY, startX, bottom);

            path.CubicTo(startX - k, bottom, left, bottom - radius + k, left, bottom - radius);
            path.LineTo(left, top + radius);
            path.CubicTo(left, top + radius - k, startX - k, top, startX, top);
            path.Close();

            // Pass 1: Soft reactive bloom halo (only when ReactiveGlow is enabled)
            if (glowAlpha > 3)
            {
                paint.StrokeWidth = strokeThickness * 2.1f;
                paint.Color = new SKColor(cr, cg, cb, glowAlpha);
                canvas.DrawPath(path, paint);
            }

            // Pass 2: Crisp core stroke
            paint.StrokeWidth = strokeThickness;
            paint.Color = new SKColor(cr, cg, cb, alpha);
            canvas.DrawPath(path, paint);
            paint.Reset();
        }

        private void SyncFromOwner()
        {
            bool seedChanged;
            ulong newSeed;
            lock (_owner._stateLock)
            {
                _state = _owner._snapshot;
                seedChanged = _owner._snapshot.SeedChanged;
                newSeed = _owner._snapshot.Seed;
                _owner._snapshot.SeedChanged = false;
                _owner._syncQueued = false;
            }

            if (seedChanged || !_rngInitialized)
            {
                _rng = new FastXorShift64(newSeed);
                _rngInitialized = true;
            }
        }

        private void ScheduleNextFrame(bool forceRegister = false)
        {
            if (forceRegister || !_frameScheduled)
            {
                _frameScheduled = true;
                RegisterForNextAnimationFrameUpdate();
            }
        }

        private void ClearPackets() => Array.Clear(_packets);

        private void ResetState()
        {
            ClearPackets();
            _signal.Reset();
            _macroEnergy = 0.0;
            _vocalMode = 0.0;
            _harmonicMode = 0.0;
            _percFlash = 0.0;
            _cooldownVocal = 0.0;
            _springPos = 0.0;
            _springVel = 0.0;
            _drainOpacity = 1f;
            _lastPulseAlpha = 0;
            _wasIdleFrame = false;
        }

        private bool HasResidualEnergy() =>
            HasActivePackets(_packets)
            || _signal.HasEnergy(0.005)
            || _macroEnergy > 0.008
            || _vocalMode > 0.006
            || _harmonicMode > 0.008
            || _percFlash > 0.006
            || Math.Abs(_springPos) > 0.005
            || Math.Abs(_springVel) > 0.02;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ComputePulseAlpha(byte colorAlpha, bool isSelected, double beat, double pulsePhase)
        {
            double sine = (Math.Sin(pulsePhase) + 1.0) * 0.5;
            double alphaRatio = isSelected
                ? Math.Clamp(0.70 + (0.30 * sine) + (0.15 * beat), 0.70, 1.0)
                : Math.Clamp(0.18 + (0.55 * sine) + (0.20 * beat), 0.15, 1.0);
            return (byte)Math.Clamp((int)Math.Round(colorAlpha * alphaRatio), 0, 255);
        }

        private double GetWaveSpeedMultiplier() => _state.Speed switch
        {
            TrackAnimationSpeed.VerySlow => 0.55,
            TrackAnimationSpeed.Slow => 0.78,
            TrackAnimationSpeed.Medium => 1.0,
            TrackAnimationSpeed.Fast => 1.30,
            TrackAnimationSpeed.Epileptic => 1.75,
            _ => 1.0
        };

        private double GetPulseSpeedMultiplier() => _state.Speed switch
        {
            TrackAnimationSpeed.VerySlow => 0.25,
            TrackAnimationSpeed.Slow => 0.50,
            TrackAnimationSpeed.Medium => 1.0,
            TrackAnimationSpeed.Fast => 2.0,
            TrackAnimationSpeed.Epileptic => 5.5,
            _ => 1.0
        };

        private (bool needsRedraw, bool continueAnim) TickVisualizerFrame(double dt, bool canRun)
        {
            double volOverdrive = Math.Clamp(_state.VolumeFactor, 1.0, 1.60);
            double volExcess = volOverdrive - 1.0;
            double speedMult = GetWaveSpeedMultiplier() * (1.0 + (0.22 * volExcess));
            double pulseMultiplier = GetPulseSpeedMultiplier() * (1.0 + (0.25 * volExcess));

            bool hasActiveAudio = false;
            if (canRun && AudioVisualizerBuffer.TryReadSnapshot(out var snap))
            {
                hasActiveAudio = true;
                double ampBoost = 1.0 + (0.35 * volExcess);
                double macroAlpha = snap.MacroEnergy > _macroEnergy
                    ? (1.0 - Math.Exp(-dt / 0.065))
                    : (1.0 - Math.Exp(-dt / 0.280));
                _macroEnergy += (snap.MacroEnergy - _macroEnergy) * macroAlpha;

                _vocalMode += (snap.Vocal - _vocalMode) * (1.0 - Math.Exp(-dt / (snap.Vocal > _vocalMode ? 0.055 : 0.180)));
                _harmonicMode += (snap.HarmonicEnergy - _harmonicMode) * (1.0 - Math.Exp(-dt / 0.120));
                _centroid += (snap.SpectralCentroid - _centroid) * (1.0 - Math.Exp(-dt / 0.100));

                double rawPercFlash = Math.Clamp(
                    (snap.SnareFlux * 0.85) + (snap.Beat * 0.60) + (snap.DropImpact * 0.90) + (snap.MidFlux * 0.42) + (snap.HighFlux * 0.32),
                    0.0,
                    1.0);
                _percFlash = Math.Max(rawPercFlash, _percFlash * Math.Exp(-dt / 0.095));

                _signal.AdvanceCooldowns(dt);
                _cooldownVocal -= dt;

                if (_state.UseWave)
                {
                    double macroGain = (0.85 + (0.48 * Math.Clamp(_macroEnergy, 0.0, 1.35))) * ampBoost;
                    float jitter = (float)_rng.NextDouble(0.92, 1.08);

                    double lowRise = snap.Low - _signal.Low;
                    double midRise = snap.Mid - _signal.Mid;
                    double highRise = snap.High - _signal.High;

                    // 1. Kick drum & Drop -> crisp zero-mean Ricker crest
                    double kickDrive = Math.Max(snap.Beat, snap.BassFlux * 0.92);
                    if (kickDrive > 0.16 && _signal.CooldownBeat <= 0.0)
                    {
                        float kickAmp = (float)Math.Clamp((0.85 + (kickDrive * 2.10) + (snap.DropImpact * 0.80)) * macroGain * jitter, 1.15, 3.10);
                        SpawnPacket(_packets, WavePacketKind.KickBullet, kickAmp, (float)_centroid, speedMult, jitter);
                        _springVel += (kickDrive * 7.8) + (snap.DropImpact * 4.5);
                        _signal.CooldownBeat = 0.16;
                    }

                    // 2. Rolling bass swell -> onset flux or band self-rise fallback for compressed mixes
                    if (((snap.BassFlux > 0.13 && snap.Low > 0.14) || (lowRise > 0.032 && snap.Low > 0.16))
                        && _signal.CooldownLow <= 0.0
                        && _signal.CooldownBeat < 0.06)
                    {
                        double bassDrive = Math.Max(snap.BassFlux, Math.Clamp(lowRise * 5.0, 0.0, 0.85));
                        float bassAmp = (float)Math.Clamp((0.70 + (bassDrive * 1.65) + (snap.Low * 0.45)) * macroGain * jitter, 0.95, 2.55);
                        SpawnPacket(_packets, WavePacketKind.BassSwell, bassAmp, (float)_centroid, speedMult, jitter);
                        _signal.CooldownLow = 0.28;
                    }

                    // 3. Melodic / Synth / Vocal attacks -> onset flux, band self-rise, or sustained vocal shimmer
                    if ((snap.MidFlux > 0.11 || (midRise > 0.032 && snap.Mid > 0.16)) && _signal.CooldownMid <= 0.0)
                    {
                        double midDrive = Math.Max(snap.MidFlux, Math.Clamp(midRise * 5.0, 0.0, 0.85));
                        float midAmp = (float)Math.Clamp((0.60 + (midDrive * 1.75) + (snap.Vocal * 0.45)) * macroGain * jitter, 0.85, 2.40);
                        SpawnPacket(_packets, WavePacketKind.MidRipple, midAmp, (float)_centroid, speedMult, jitter);
                        _springVel += midDrive * 3.8;
                        _signal.CooldownMid = 0.18;
                    }
                    else if ((snap.Vocal > 0.28 || snap.HarmonicEnergy > 0.42) && _cooldownVocal <= 0.0)
                    {
                        float vocalAmp = (float)Math.Clamp((0.55 + (snap.Vocal * 1.15) + (snap.HarmonicEnergy * 0.40)) * macroGain * jitter, 0.75, 1.95);
                        SpawnPacket(_packets, WavePacketKind.MidRipple, vocalAmp, (float)_centroid, speedMult * 0.90, jitter);
                        _cooldownVocal = 0.30;
                    }

                    // 4. Snare / Hi-Hat / Cymbals -> narrow fast splashes overtaking bass swells
                    double rawHiFlux = Math.Max(snap.SnareFlux * 1.05, snap.HighFlux);
                    if ((rawHiFlux > 0.14 || (highRise > 0.030 && snap.High > 0.14)) && _signal.CooldownHigh <= 0.0)
                    {
                        double hiDrive = Math.Max(rawHiFlux, Math.Clamp(highRise * 5.5, 0.0, 0.85));
                        float hiAmp = (float)Math.Clamp((0.50 + (hiDrive * 1.60)) * macroGain * jitter, 0.70, 2.15);
                        SpawnPacket(_packets, WavePacketKind.HighNeedle, hiAmp, (float)_centroid, speedMult, jitter);
                        if (snap.SnareFlux > 0.22)
                            _springVel += snap.SnareFlux * 4.8;
                        _signal.CooldownHigh = 0.15;
                    }
                }

                _vocalPhase = (_vocalPhase + dt * (4.0 + (5.5 * _centroid) + (2.4 * _vocalMode)) * speedMult) % (Math.Tau * 64.0);
                _signal.SmoothToward(snap.Beat, snap.Low, snap.Mid, snap.High, dt, 0.065, 0.110, 0.095, 0.065);
                _pulsePhase = (_pulsePhase + (BasePulseSpeed * pulseMultiplier * dt)) % Math.Tau;
            }
            else
            {
                double decay = Math.Exp(-dt / 0.200);
                _signal.Decay(dt, 0.180);
                _macroEnergy *= decay;
                _vocalMode *= decay;
                _harmonicMode *= decay;
                _percFlash *= Math.Exp(-dt / 0.130);
            }

            // Integrate 2nd-order spring for reactive stroke weight & bloom bounce
            double springForce = (-210.0 * _springPos) - (18.5 * _springVel);
            _springVel += springForce * dt;
            _springPos = Math.Clamp(_springPos + (_springVel * dt), -0.15, 1.25);

            // Advance traveling left-to-right packets (with silky PauseFade when paused/silent)
            if (_state.UseWave)
                StepPackets(_packets, dt, speedMult, isPlaying: canRun && hasActiveAudio);

            bool isDraining = _state.IsDraining && !_state.IsActive;
            if (isDraining)
                _drainOpacity = Math.Max(0f, _drainOpacity - (float)(dt * 2.4));
            else
                _drainOpacity = 1f;

            if (canRun && hasActiveAudio)
                PublishSharedWaveState(_state.TrackId, _packets, in _signal, _pulsePhase, _macroEnergy, _vocalMode);

            bool hasEnergy = HasResidualEnergy();
            bool needsRedraw;
            bool continueAnim;

            if (_state.UseWave)
            {
                if (isDraining)
                {
                    continueAnim = hasEnergy && _drainOpacity > 0.004f;
                    needsRedraw = true;
                }
                else
                {
                    needsRedraw = hasEnergy || !_wasIdleFrame;
                    _wasIdleFrame = !hasEnergy;
                    continueAnim = hasEnergy || hasActiveAudio;
                }
            }
            else
            {
                byte rawPulse = ComputePulseAlpha(_state.Color.Alpha, _state.IsSelected, _signal.Beat, _pulsePhase);
                byte scaledPulse = (byte)Math.Clamp((int)MathF.Round(rawPulse * _drainOpacity), 0, 255);
                if (isDraining)
                {
                    needsRedraw = scaledPulse != _lastPulseAlpha || _drainOpacity > 0.004f;
                    continueAnim = _drainOpacity > 0.004f;
                }
                else
                {
                    needsRedraw = scaledPulse != _lastPulseAlpha || hasEnergy;
                    continueAnim = canRun || hasEnergy;
                }
                _lastPulseAlpha = scaledPulse;
                _wasIdleFrame = false;
            }

            return (needsRedraw, continueAnim);
        }
    }

    /// <summary>
    /// Spawns a traveling left-to-right wave packet directly at the left entrance so it becomes visible
    /// within the 0..80 ms audiovisual binding window. Never hard-evicts an active mid-rail packet.
    /// </summary>
    internal static void SpawnPacket(
        Span<WavePacket> packets,
        WavePacketKind kind,
        float amplitude,
        float centroid,
        double speedMult,
        float jitter = 1.0f)
    {
        int freeSlot = -1;
        int mergeEntrySlot = -1;
        for (int i = 0; i < packets.Length; i++)
        {
            if (!packets[i].IsActive)
            {
                freeSlot = i;
                break;
            }

            if (mergeEntrySlot < 0 && packets[i].Kind == kind && packets[i].Position < 0.16f)
                mergeEntrySlot = i;
        }

        if (freeSlot < 0)
        {
            // Pool saturated: coalesce smoothly into a fresh left-edge packet instead of popping a visible mid-rail wave
            if (mergeEntrySlot >= 0)
            {
                ref var existing = ref packets[mergeEntrySlot];
                existing.Amplitude = MathF.Min(2.75f, MathF.Max(existing.Amplitude, amplitude) + (amplitude * 0.16f));
                existing.PauseFade = 1f;
            }
            return;
        }

        float clampedCentroid = Math.Clamp(centroid, 0f, 1f);
        float speedScale = (float)Math.Clamp(speedMult, 0.35, 2.5);
        float halfWidth;
        float baseSpeed;
        float frequency;
        float maxAge;

        switch (kind)
        {
            case WavePacketKind.KickBullet:
                halfWidth = 0.125f * jitter;
                baseSpeed = 0.54f * jitter;
                frequency = 0.65f;
                maxAge = 3.2f;
                break;

            case WavePacketKind.BassSwell:
                halfWidth = 0.175f * jitter;
                baseSpeed = 0.42f * jitter;
                frequency = 0.50f;
                maxAge = 3.8f;
                break;

            case WavePacketKind.MidRipple:
                halfWidth = 0.105f * jitter;
                baseSpeed = 0.66f * jitter;
                frequency = 1.60f + (2.40f * clampedCentroid);
                maxAge = 2.6f;
                break;

            default: // HighNeedle
                halfWidth = 0.066f * jitter;
                baseSpeed = 0.88f * jitter;
                frequency = 3.20f + (2.60f * clampedCentroid);
                maxAge = 2.0f;
                break;
        }

        ref var p = ref packets[freeSlot];
        p.IsActive = true;
        // Start right at the left edge so leading crest enters [0..1] immediately at t = 0 ms
        p.Position = 0.015f;
        p.HalfWidth = halfWidth;
        p.Speed = baseSpeed * speedScale;
        p.Amplitude = Math.Clamp(amplitude, 0.1f, 3.2f);
        p.Frequency = frequency;
        p.Phase = 0f;
        p.Age = 0f;
        p.MaxAge = maxAge / MathF.Max(0.45f, speedScale);
        p.PauseFade = 1f;
        p.Kind = kind;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothStepF(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputePacketEffectiveAmplitude(in WavePacket p)
    {
        if (!p.IsActive) return 0f;

        // Smooth 80 ms entry ramp (0 -> 1)
        float attack = SmoothStepF(p.Age * (1f / PacketAttackSeconds));

        // Smooth life tail fade: stays full until 72% of MaxAge, then dissolves to exact 0.0 at MaxAge
        float lifeFrac = Math.Clamp(p.Age / MathF.Max(0.1f, p.MaxAge), 0f, 1f);
        float lifeFade = 1f - SmoothStepF((lifeFrac - 0.72f) * (1f / 0.28f));

        // Smooth right-edge exit fade: dissolves to exact 0.0 as the packet glides off the right corner
        float exitStart = 1.0f - (p.HalfWidth * 0.35f);
        float exitSpan = MathF.Max(0.08f, p.HalfWidth * 2.55f);
        float exitFade = 1f - SmoothStepF((p.Position - exitStart) / exitSpan);

        float travelDamp = MathF.Exp(-p.Age * 0.16f);
        return p.Amplitude * attack * lifeFade * exitFade * travelDamp * p.PauseFade;
    }

    /// <summary>
    /// Advances all active wave packets strictly left-to-right with zero allocations and smooth deactivation
    /// only after the packet has dissolved to zero.
    /// </summary>
    internal static bool StepPackets(Span<WavePacket> packets, double dt, double speedMult, bool isPlaying = true)
    {
        float fdt = (float)dt;
        float phaseStep = fdt * (float)(4.5 * Math.Clamp(speedMult, 0.35, 2.5));
        float pauseDecay = isPlaying ? 1f : MathF.Exp(-fdt / 0.42f);
        bool anyActive = false;

        for (int i = 0; i < packets.Length; i++)
        {
            ref var p = ref packets[i];
            if (!p.IsActive) continue;

            p.Age += fdt;
            p.Position += p.Speed * fdt;
            p.Phase += phaseStep * p.Frequency;
            if (!isPlaying)
                p.PauseFade *= pauseDecay;

            // Deactivate ONLY once lifeFade == 0, exitFade == 0, or PauseFade < 0.3% (zero visual snap)
            float exitCompletePos = 1.0f + (p.HalfWidth * 2.25f);
            if (p.Age >= p.MaxAge || p.Position >= exitCompletePos || p.PauseFade < 0.003f)
            {
                p.IsActive = false;
                continue;
            }

            anyActive = true;
        }

        return anyActive;
    }

    /// <summary>
    /// Compacts active wave packets onto the stack and evaluates the symmetric rail displacement curve once (0 B heap alloc).
    /// Uses zero-mean bipolar bass/kick wavelets + adaptive density headroom + smooth tanh ceiling so bass-heavy tracks
    /// never peg flat at maximum displacement.
    /// </summary>
    internal static void EvaluateRailDisplacement(
        ReadOnlySpan<WavePacket> packets,
        Span<float> disp,
        float vocalAmp,
        float harmAmp,
        float vocalPhase,
        float centroid)
    {
        int n = disp.Length;
        if (n < 3)
        {
            disp.Clear();
            return;
        }

        disp[0] = 0f;
        disp[n - 1] = 0f;

        Span<ActiveWavePacket> active = stackalloc ActiveWavePacket[MaxWavePackets];
        int activeCount = 0;
        float activeEnergySum = 0f;

        int limit = Math.Min(packets.Length, MaxWavePackets);
        for (int i = 0; i < limit; i++)
        {
            ref readonly var p = ref packets[i];
            if (!p.IsActive) continue;

            float effAmp = ComputePacketEffectiveAmplitude(in p);
            if (effAmp < 0.002f) continue;

            activeEnergySum += effAmp * effAmp;
            active[activeCount++] = new ActiveWavePacket
            {
                Position = p.Position,
                InvHalfWidth = 1f / MathF.Max(0.02f, p.HalfWidth),
                EffectiveAmp = effAmp,
                Frequency = p.Frequency,
                Phase = p.Phase,
                Kind = p.Kind
            };
        }

        // Adaptive wave-density headroom: keeps normal choruses punchy while preventing excessive stack buildup
        float excessEnergy = MathF.Max(0f, activeEnergySum - 9.0f);
        float densityNorm = MathF.Max(0.55f, 1f / MathF.Sqrt(1f + (excessEnergy * 0.06f)));

        float invNMinusOne = 1f / (n - 1);
        float invSafe = 1f / MaxSafeDisplacement;
        float centroidFreq = 1.4f + (Math.Clamp(centroid, 0f, 1f) * 2.8f);
        bool hasContinuousFlow = vocalAmp > 0.008f || harmAmp > 0.008f;

        for (int i = 1; i < n - 1; i++)
        {
            float u = i * invNMinusOne;
            float rawDisp = 0f;

            for (int pIdx = 0; pIdx < activeCount; pIdx++)
            {
                ref readonly var pk = ref active[pIdx];
                float dx = (u - pk.Position) * pk.InvHalfWidth;
                if (MathF.Abs(dx) >= 2.50f) continue;

                // Water crest asymmetry: crisp leading front (dx > 0), softer trailing wake (dx < 0)
                float shaped = dx > 0f ? dx * 1.16f : dx * 0.88f;
                float r2 = shaped * shaped;

                float waveVal = pk.Kind switch
                {
                    // Zero-mean Ricker water wavelet: prominent central crest + smooth side troughs (zero DC pile-up)
                    WavePacketKind.KickBullet => MathF.Exp(-r2 * 1.50f) * (1.0f - (3.0f * r2)),
                    // Rolling bipolar bass wave: breathes above & below baseline smoothly
                    WavePacketKind.BassSwell => MathF.Exp(-r2 * 1.35f) * MathF.Cos(shaped * 2.15f),
                    WavePacketKind.MidRipple => MathF.Exp(-r2 * 1.85f) * MathF.Cos((dx * pk.Frequency * 2.1f) - (pk.Phase * 0.25f)),
                    _ => MathF.Exp(-r2 * 2.05f) * MathF.Cos((dx * pk.Frequency * 2.4f) - (pk.Phase * 0.35f))
                };

                rawDisp += pk.EffectiveAmp * waveVal;
            }

            rawDisp *= densityNorm;

            if (hasContinuousFlow)
            {
                // Left-to-right flowing harmonic/vocal ripple (-vocalPhase ensures strictly rightward travel)
                float carrier1 = MathF.Sin((u * MathF.Tau * 1.5f) - vocalPhase) * 0.65f;
                float carrier2 = MathF.Sin((u * MathF.Tau * centroidFreq) - (vocalPhase * 1.35f)) * 0.35f;
                rawDisp += ((vocalAmp * carrier1) + (((vocalAmp * 0.45f) + harmAmp) * carrier2)) * 0.80f;
            }

            if (MathF.Abs(rawDisp) < DisplacementZeroEpsilon)
            {
                disp[i] = 0f;
                continue;
            }

            // Smooth C-infinity hyperbolic tangent ceiling: strictly < MaxSafeDisplacement with zero flat-top clipping
            float safeDisp = MaxSafeDisplacement * MathF.Tanh(rawDisp * invSafe);

            float edgeTaper = 1f;
            if (u < CornerBlendMargin)
            {
                float t = u * (1f / CornerBlendMargin);
                edgeTaper = t * t * t * (t * ((t * 6f) - 15f) + 10f);
            }
            else if (u > (1f - CornerBlendMargin))
            {
                float t = (1f - u) * (1f / CornerBlendMargin);
                edgeTaper = t * t * t * (t * ((t * 6f) - 15f) + 10f);
            }

            disp[i] = safeDisp * edgeTaper;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasActivePackets(ReadOnlySpan<WavePacket> packets)
    {
        for (int i = 0; i < packets.Length; i++)
        {
            if (packets[i].IsActive)
                return true;
        }
        return false;
    }

    #endregion

    #region Static Global Configuration

    private static bool _globalUseWave = true;
    private static bool _globalReactiveGlow = true;
    private static TrackAnimationSpeed _globalAnimationSpeed = TrackAnimationSpeed.Medium;
    private static bool _isGlobalConfigLoaded;
    private static PlayerControlService? _globalPlayerControl;

    public static void ConfigureGlobal(bool useWave, TrackAnimationSpeed speed, bool reactiveGlow = true)
    {
        _globalUseWave = useWave;
        _globalAnimationSpeed = speed;
        _globalReactiveGlow = reactiveGlow;
        _isGlobalConfigLoaded = true;
        GlobalConfigChanged?.Invoke();
    }

    private static event Action? GlobalConfigChanged;

    private static void EnsureGlobalConfigLoaded()
    {
        if (_isGlobalConfigLoaded && _globalPlayerControl != null) return;

        try
        {
            _globalPlayerControl ??= AppEntry.Services.GetService<PlayerControlService>();

            if (!_isGlobalConfigLoaded)
            {
                var settings = AppEntry.Services.GetService<LibraryService>()?.Settings;
                if (settings != null)
                {
                    _globalUseWave = settings.UseWaveAnimation;
                    _globalAnimationSpeed = settings.TrackAnimationSpeed;
                    _globalReactiveGlow = settings.WaveBorderReactiveGlow;
                    _isGlobalConfigLoaded = true;
                }
            }
        }
        catch
        {
            // Designer guard
        }
    }

    #endregion

    #region Fields

    private static readonly Lock s_sharedWaveLock = new();
    private static readonly WavePacket[] s_sharedPackets = new WavePacket[MaxWavePackets];
    private static AudioReactiveSignalState s_sharedSignal;
    private static double s_sharedPulsePhase;
    private static double s_sharedMacroEnergy;
    private static double s_sharedVocalMode;
    private static string? s_sharedTrackId;
    private static long s_sharedTimestamp;
    private static bool s_hasSharedWave;

    private static void PublishSharedWaveState(
        string? trackId,
        WavePacket[] packets,
        in AudioReactiveSignalState signal,
        double pulsePhase,
        double macroEnergy,
        double vocalMode)
    {
        lock (s_sharedWaveLock)
        {
            s_sharedTrackId = trackId;
            Array.Copy(packets, s_sharedPackets, MaxWavePackets);
            s_sharedSignal = signal;
            s_sharedPulsePhase = pulsePhase;
            s_sharedMacroEnergy = macroEnergy;
            s_sharedVocalMode = vocalMode;
            s_sharedTimestamp = Stopwatch.GetTimestamp();
            s_hasSharedWave = true;
        }
    }

    private static void TryRestoreSharedWaveState(
        string? trackId,
        WavePacket[] targetPackets,
        ref AudioReactiveSignalState signal,
        ref double pulsePhase,
        ref double macroEnergy,
        ref double vocalMode)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec;

        lock (s_sharedWaveLock)
        {
            if (!s_hasSharedWave)
                return;

            if (!string.IsNullOrEmpty(trackId) && !string.Equals(trackId, s_sharedTrackId, StringComparison.Ordinal))
                return;

            elapsedSec = Stopwatch.GetElapsedTime(s_sharedTimestamp, now).TotalSeconds;
            if (elapsedSec > 2.5)
                return;

            Array.Copy(s_sharedPackets, targetPackets, MaxWavePackets);
            signal = s_sharedSignal;
            pulsePhase = s_sharedPulsePhase;
            macroEnergy = s_sharedMacroEnergy;
            vocalMode = s_sharedVocalMode;
        }

        double dt = Math.Clamp(elapsedSec, 0.0, 0.5);
        pulsePhase = (pulsePhase + (BasePulseSpeed * dt)) % Math.Tau;
        StepPackets(targetPackets, dt, speedMult: 1.0, isPlaying: true);
    }

    private readonly Action _globalConfigChangedHandler;
    private readonly Action<double> _volumeOverdriveChangedHandler;
    private readonly Action _completeDrainCallback;
    private readonly EventHandler<AvaloniaPropertyChangedEventArgs> _brushPropertyChangedHandler;
    private readonly Lock _stateLock = new();

    private CompositionSilenceMonitor? _silenceMonitor;
    private CompositionCustomVisual? _customVisual;
    private SolidColorBrush? _subscribedBrush;
    private object? _activeDataContext;
    private WaveStateSnapshot _snapshot;
    private ulong _currentSeed;
    private bool _syncQueued;
    private bool _isAttached;
    private bool _isDraining;
    private bool _isGlobalSubscribed;
    private bool _isVolumeSubscribed;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool>(nameof(IsSelected));

    public static readonly StyledProperty<bool?> UseWaveAnimationProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool?>(nameof(UseWaveAnimation));

    public static readonly StyledProperty<bool?> ReactiveGlowProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool?>(nameof(ReactiveGlow));

    public static readonly StyledProperty<TrackAnimationSpeed?> AnimationSpeedProperty =
        AvaloniaProperty.Register<AudioWaveBorder, TrackAnimationSpeed?>(nameof(AnimationSpeed));

    public static readonly StyledProperty<IBrush?> WaveBrushProperty =
        AvaloniaProperty.Register<AudioWaveBorder, IBrush?>(nameof(WaveBrush));

    public static readonly StyledProperty<double> WaveThicknessProperty =
        AvaloniaProperty.Register<AudioWaveBorder, double>(nameof(WaveThickness), DefaultBorderThickness);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<AudioWaveBorder, CornerRadius>(nameof(CornerRadius), new CornerRadius(8));

    public static readonly StyledProperty<int?> SeedProperty =
        AvaloniaProperty.Register<AudioWaveBorder, int?>(nameof(Seed));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool? UseWaveAnimation
    {
        get => GetValue(UseWaveAnimationProperty);
        set => SetValue(UseWaveAnimationProperty, value);
    }

    public bool? ReactiveGlow
    {
        get => GetValue(ReactiveGlowProperty);
        set => SetValue(ReactiveGlowProperty, value);
    }

    public TrackAnimationSpeed? AnimationSpeed
    {
        get => GetValue(AnimationSpeedProperty);
        set => SetValue(AnimationSpeedProperty, value);
    }

    public IBrush? WaveBrush
    {
        get => GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public double WaveThickness
    {
        get => GetValue(WaveThicknessProperty);
        set => SetValue(WaveThicknessProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public int? Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    #endregion

    #region Constructor & Static Init

    static AudioWaveBorder()
    {
        IsActiveProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnActiveChanged());
        IsPlayingProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        IsSelectedProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        UseWaveAnimationProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        ReactiveGlowProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        AnimationSpeedProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        WaveThicknessProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        CornerRadiusProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnVisualStatePropertyChanged());
        WaveBrushProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnWaveBrushChanged());
    }

    public AudioWaveBorder()
    {
        _globalConfigChangedHandler = OnGlobalConfigChanged;
        _volumeOverdriveChangedHandler = _ =>
        {
            if (IsActive)
                PushStateToHandler(seedChanged: false);
        };
        _completeDrainCallback = CompleteDrainOnUiThread;
        _brushPropertyChangedHandler = OnSubscribedBrushPropertyChanged;
        _currentSeed = (ulong)Stopwatch.GetTimestamp() ^ 0x9E3779B97F4A7C15UL;
    }

    #endregion

    #region Lifecycle & Sync

    private void OnGlobalConfigChanged()
    {
        if (IsActive)
            PushStateToHandler(seedChanged: false);
    }

    private void OnVisualStatePropertyChanged()
    {
        if (IsActive)
            PushStateToHandler(seedChanged: false);
    }

    private void OnWaveBrushChanged()
    {
        if (_isAttached && IsActive)
        {
            UpdateWaveBrushSubscription(WaveBrush);
            PushStateToHandler(seedChanged: false);
        }
    }

    private void OnSubscribedBrushPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SolidColorBrush.ColorProperty && IsActive)
            PushStateToHandler(seedChanged: false);
    }

    private void UpdateWaveBrushSubscription(IBrush? brush)
    {
        var next = brush as SolidColorBrush;
        if (ReferenceEquals(_subscribedBrush, next))
            return;

        if (_subscribedBrush != null)
            _subscribedBrush.PropertyChanged -= _brushPropertyChangedHandler;

        _subscribedBrush = next;
        if (_subscribedBrush != null)
            _subscribedBrush.PropertyChanged += _brushPropertyChangedHandler;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (!ReferenceEquals(DataContext, _activeDataContext))
        {
            _activeDataContext = IsActive ? DataContext : null;
            if (!IsActive && _isDraining)
            {
                _isDraining = false;
                DeactivateVisual();
            }
            else if (IsActive && _isAttached && _customVisual != null)
            {
                PushStateToHandler(seedChanged: true);
            }
        }
    }

    private void OnActiveChanged()
    {
        if (IsActive)
        {
            _isDraining = false;
            _activeDataContext = DataContext;
            if (_isAttached)
            {
                EnsureGlobalConfigLoaded();
                ActivateVisual();
            }
        }
        else if (_isAttached
              && _customVisual != null
              && ReferenceEquals(DataContext, _activeDataContext)
              && (_silenceMonitor?.CanAnimateNow ?? false))
        {
            _isDraining = true;
            PushStateToHandler(seedChanged: false);
        }
        else
        {
            _isDraining = false;
            _activeDataContext = null;
            DeactivateVisual();
        }
    }

    private void PostDrainComplete() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(_completeDrainCallback, Avalonia.Threading.DispatcherPriority.Background);

    private void CompleteDrainOnUiThread()
    {
        if (!_isDraining || IsActive)
            return;

        _isDraining = false;
        _activeDataContext = null;
        DeactivateVisual();
    }

    private void ActivateVisual()
    {
        if (!_isGlobalSubscribed)
        {
            GlobalConfigChanged += _globalConfigChangedHandler;
            _isGlobalSubscribed = true;
        }

        if (!_isVolumeSubscribed && _globalPlayerControl != null)
        {
            _globalPlayerControl.VolumeOverdriveChanged += _volumeOverdriveChangedHandler;
            _isVolumeSubscribed = true;
        }

        UpdateWaveBrushSubscription(WaveBrush);

        _silenceMonitor ??= new CompositionSilenceMonitor(
            this,
            shouldAnimate: () => IsActive || _isDraining,
            onStateReevaluated: () => PushStateToHandler(seedChanged: false),
            onResumeFromSilence: () => _customVisual?.SendHandlerMessage(MsgResumeFromSilence),
            shouldPollSilence: () => IsActive && IsPlaying);
        _silenceMonitor.Attach();

        if (_customVisual == null && ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            _customVisual = compositor.CreateCustomVisual(new WaveBorderHandler(this));
            _customVisual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _customVisual);
            lock (_stateLock)
            {
                WriteSnapshotLocked(seedChanged: true);
            }
            _customVisual.SendHandlerMessage(MsgStart);
        }
        else
        {
            PushStateToHandler(seedChanged: false);
        }
    }

    private void DeactivateVisual()
    {
        if (_isGlobalSubscribed)
        {
            GlobalConfigChanged -= _globalConfigChangedHandler;
            _isGlobalSubscribed = false;
        }

        if (_isVolumeSubscribed && _globalPlayerControl != null)
        {
            _globalPlayerControl.VolumeOverdriveChanged -= _volumeOverdriveChangedHandler;
            _isVolumeSubscribed = false;
        }

        UpdateWaveBrushSubscription(null);
        _silenceMonitor?.Detach();

        if (_customVisual != null)
        {
            _customVisual.SendHandlerMessage(MsgStop);
            ElementComposition.SetElementChildVisual(this, null);
            _customVisual = null;
        }

        _activeDataContext = null;

        lock (_stateLock)
        {
            _syncQueued = false;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;

        if (IsActive)
        {
            _isDraining = false;
            _activeDataContext = DataContext;
            EnsureGlobalConfigLoaded();
            ActivateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _isDraining = false;
        DeactivateVisual();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeedProperty)
        {
            int? seed = change.GetNewValue<int?>();
            _currentSeed = seed.HasValue
                ? (ulong)seed.Value * 0x9E3779B97F4A7C15UL
                : (ulong)Stopwatch.GetTimestamp() ^ 0x9E3779B97F4A7C15UL;

            if (IsActive)
                PushStateToHandler(seedChanged: true);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child != null)
        {
            Child.Measure(availableSize);
            return Child.DesiredSize;
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        if (_customVisual != null)
        {
            var prevSize = _customVisual.Size;
            var size = new Vector(finalSize.Width, finalSize.Height);
            if (prevSize != size)
            {
                _customVisual.Size = size;
                if (prevSize.X <= 0.0 || prevSize.Y <= 0.0)
                    PushStateToHandler(seedChanged: false);
            }
        }
        return finalSize;
    }

    private void WriteSnapshotLocked(bool seedChanged)
    {
        var color = ResolveWaveColor();
        float thickness = (float)(IsSelected ? SelectedBorderThickness : WaveThickness);

        bool canAnimate = _silenceMonitor?.CanAnimateNow ?? false;
        if (!canAnimate || !IsPlaying)
            _silenceMonitor?.StopIdleTimer();

        bool preserveSeedFlag = _snapshot.SeedChanged || seedChanged;
        _snapshot = new WaveStateSnapshot
        {
            IsActive = IsActive,
            IsDraining = _isDraining,
            IsPlaying = IsPlaying,
            IsSelected = IsSelected,
            UseWave = UseWaveAnimation ?? _globalUseWave,
            ReactiveGlow = ReactiveGlow ?? _globalReactiveGlow,
            Speed = AnimationSpeed ?? _globalAnimationSpeed,
            Thickness = thickness,
            CornerRadius = (float)CornerRadius.TopLeft,
            VolumeFactor = Math.Clamp(_globalPlayerControl?.VolumeOverdriveFactor ?? 1.0, 1.0, 1.60),
            TrackId = (DataContext as LMP.UI.Features.Shared.TrackItemViewModel)?.Id,
            Color = color,
            Seed = _currentSeed,
            SeedChanged = preserveSeedFlag,
            CanAnimate = canAnimate
        };
    }

    private void PushStateToHandler(bool seedChanged)
    {
        if (!_isAttached || _customVisual == null) return;

        bool shouldSend = false;
        lock (_stateLock)
        {
            WriteSnapshotLocked(seedChanged);
            if (!_syncQueued)
            {
                _syncQueued = true;
                shouldSend = true;
            }
        }

        if (shouldSend)
            _customVisual.SendHandlerMessage(MsgSyncState);
    }

    private SKColor ResolveWaveColor()
    {
        if (WaveBrush is ISolidColorBrush solid)
        {
            var c = solid.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        if (this.TryFindResource("AccentBrush", out var res) && res is ISolidColorBrush fallback)
        {
            var c = fallback.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new SKColor(30, 144, 255, 255);
    }

    #endregion
}