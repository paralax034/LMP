using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace LMP.UI.Controls;

/// <summary>
/// Border decorator with a continuous hydrodynamic waveguide, where the wavelength represents the note pitch.
/// Runs 60 FPS animations inside <see cref="CompositionCustomVisualHandler"/> on the render thread
/// with lazy per-row allocation (inactive rows hold zero compositor resources).
/// </summary>
public sealed class AudioWaveBorder : Decorator
{
    #region Constants

    private const double DefaultBorderThickness = 1.35;
    private const double SelectedBorderThickness = 2.4;
    private const float InsetMarginX = 1.5f;
    private const float InsetMarginY = 3.8f;

    private const double BaseWaveSpeed = 0.70;
    private const double BasePulseSpeed = 1.4;
    private const double MinFrameIntervalSeconds = 0.0125;
    private const double MaxFrameDeltaSeconds = 0.050;

    private const float CornerBlendMargin = 0.08f;
    private const float MaxSafeDisplacement = 2.6f;
    private const double MinSpatialSpacing = 0.28;

    private const int MaxActivePackets = 16;
    private const float SampleStepPx = 16.0f;
    private const int MinRailSamples = 20;
    private const int MaxRailSamples = 64;
    private const float DisplacementZeroEpsilon = 1e-5f;
    private const float BezierCircleConstant = 0.5522847498f;

    private static readonly object MsgStart = new();
    private static readonly object MsgStop = new();
    private static readonly object MsgResumeFromSilence = new();
    private static readonly object MsgSyncState = new();

    #endregion

    #region Wave Packet Structs & Composition Handler

    private enum PacketKind : byte
    {
        KickBullet,
        BassSwell,
        MidRipple,
        HighNeedle
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct WavePacket
    {
        public double Position;
        public double Velocity;
        public double Amplitude;
        public double Width;
        public double Frequency;
        public double PhaseOffset;
        public double HarmonicRatio;
        public double HarmonicPhase;
        public double DecayRate;
        public bool IsActive;
    }

    private struct WaveStateSnapshot
    {
        public bool IsActive;
        public bool IsDraining;
        public bool IsPlaying;
        public bool IsSelected;
        public bool UseWave;
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
        private readonly WavePacket[] _packets = new WavePacket[MaxActivePackets];

        private WaveStateSnapshot _state;
        private AudioReactiveSignalState _signal;
        private FastXorShift64 _rng;
        private bool _rngInitialized;
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
            double pad = MaxSafeDisplacement + _state.Thickness + 1.0;
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

                if (_state.CanAnimate && _state.IsActive && _state.IsPlaying && !HasActivePackets())
                    TryRestoreSharedWaveState(_state.TrackId, _packets, ref _signal, ref _pulsePhase);

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
                    Array.Clear(_packets);
                    _lastPulseAlpha = 0;
                }

                if (_state.IsActive && _state.IsPlaying && !HasActivePackets())
                    TryRestoreSharedWaveState(_state.TrackId, _packets, ref _signal, ref _pulsePhase);

                if (!_state.IsActive && _state.IsDraining && _state.UseWave && !HasActivePackets() && !_signal.HasEnergy(0.01))
                {
                    _running = false;
                    ResetState();
                    Invalidate();
                    _owner.PostDrainComplete();
                    return;
                }

                Invalidate();

                bool shouldRun = (_state.IsActive && _state.IsPlaying)
                              || HasActivePackets()
                              || _signal.HasEnergy(0.01)
                              || (!_state.IsActive && _state.IsDraining && _drainOpacity > 0.01f);

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
                _signal.Reset();
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

            byte baseAlpha = _state.UseWave
                ? _state.Color.Alpha
                : ComputePulseAlpha(_state.Color.Alpha, _state.IsSelected, _signal.Beat, _pulsePhase);

            byte alpha = (byte)Math.Clamp((int)Math.Round(baseAlpha * lease.CurrentOpacity * _drainOpacity), 0, 255);
            if (alpha == 0) return;

            var canvas = lease.SkCanvas;
            var paint = t_paint ??= new SKPaint();
            paint.Reset();
            paint.IsAntialias = true;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = _state.Thickness;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.Color = new SKColor(_state.Color.Red, _state.Color.Green, _state.Color.Blue, alpha);

            float left = InsetMarginX;
            float top = InsetMarginY;
            float right = InsetMarginX + width;
            float bottom = InsetMarginY + height;

            if (!_state.UseWave || !HasActivePackets())
            {
                canvas.DrawRoundRect(new SKRect(left, top, right, bottom), radius, radius, paint);
                paint.Reset();
                return;
            }

            float horizLen = width - (2f * radius);
            int railSamples = Math.Clamp((int)(horizLen / SampleStepPx), MinRailSamples, MaxRailSamples);
            float invSamplesMinusOne = 1f / (railSamples - 1);
            float k = radius * BezierCircleConstant;

            var path = t_path ??= new SKPath();
            path.Rewind();

            float startX = left + radius;
            float topY0 = top - CalculateDisplacement(0f);
            path.MoveTo(startX, topY0);

            float prevX = startX;
            float prevY = topY0;
            for (int i = 1; i < railSamples - 1; i++)
            {
                float u = i * invSamplesMinusOne;
                float curX = startX + (u * horizLen);
                float curY = top - CalculateDisplacement(u);
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
            float topYEnd = top - CalculateDisplacement(1f);
            path.QuadTo(prevX, prevY, rightInnerX, topYEnd);

            path.CubicTo(rightInnerX + k, top, right, top + radius - k, right, top + radius);
            path.LineTo(right, bottom - radius);

            path.CubicTo(right, bottom - radius + k, rightInnerX + k, bottom, rightInnerX, bottom);

            float bottomY0 = bottom + CalculateDisplacement(1f);
            prevX = rightInnerX;
            prevY = bottomY0;
            for (int i = 1; i < railSamples - 1; i++)
            {
                float u = 1f - (i * invSamplesMinusOne);
                float curX = startX + (u * horizLen);
                float curY = bottom + CalculateDisplacement(u);
                float midX = (prevX + curX) * 0.5f;
                float midY = (prevY + curY) * 0.5f;
                if (i == 1)
                    path.LineTo(midX, midY);
                else
                    path.QuadTo(prevX, prevY, midX, midY);

                prevX = curX;
                prevY = curY;
            }

            float bottomYEnd = bottom + CalculateDisplacement(0f);
            path.QuadTo(prevX, prevY, startX, bottomYEnd);

            path.CubicTo(startX - k, bottom, left, bottom - radius + k, left, bottom - radius);
            path.LineTo(left, top + radius);

            path.CubicTo(left, top + radius - k, startX - k, top, startX, top);
            path.Close();

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

        private void ResetState()
        {
            Array.Clear(_packets);
            _signal.Reset();
            _drainOpacity = 1f;
            _lastPulseAlpha = 0;
            _wasIdleFrame = false;
        }

        private bool HasActivePackets()
        {
            for (int i = 0; i < _packets.Length; i++)
            {
                if (_packets[i].IsActive) return true;
            }
            return false;
        }

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
            TrackAnimationSpeed.VerySlow => 0.32,
            TrackAnimationSpeed.Slow => 0.60,
            TrackAnimationSpeed.Medium => 1.0,
            TrackAnimationSpeed.Fast => 1.55,
            TrackAnimationSpeed.Epileptic => 2.40,
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
            double waveMultiplier = GetWaveSpeedMultiplier() * (1.0 + (0.30 * volExcess));
            double pulseMultiplier = GetPulseSpeedMultiplier() * (1.0 + (0.25 * volExcess));

            if (canRun)
            {
                double curLow = AudioVisualizerBuffer.LowLevel;
                double curMid = AudioVisualizerBuffer.MidLevel;
                double curHigh = AudioVisualizerBuffer.HighLevel;
                double curBeat = AudioVisualizerBuffer.BeatPulse;

                double effBeat = Math.Pow(curBeat, 0.75);
                double effLow = Math.Pow(curLow, 0.75);
                double effMid = Math.Pow(curMid, 0.85);
                double effHigh = Math.Pow(curHigh, 0.85);

                _signal.AdvanceCooldowns(dt);

                if (_state.UseWave)
                {
                    double baseVelocity = BaseWaveSpeed * waveMultiplier;
                    double minCooldown = MinSpatialSpacing / Math.Max(0.15, baseVelocity);
                    double ampBoost = 1.0 + (0.55 * volExcess);

                    if (effBeat > 0.18 && curBeat > _signal.Beat && _signal.CooldownBeat <= 0.0)
                    {
                        SpawnPacket(PacketKind.KickBullet, effBeat * 3.4 * ampBoost, baseVelocity);
                        _signal.CooldownBeat = minCooldown * 0.95;
                    }

                    if ((effLow - _signal.Low) > 0.025 && effLow > 0.12 && _signal.CooldownLow <= 0.0)
                    {
                        SpawnPacket(PacketKind.BassSwell, effLow * 2.8 * ampBoost, baseVelocity);
                        _signal.CooldownLow = minCooldown * 1.10;
                    }

                    if ((effMid - _signal.Mid) > 0.028 && effMid > 0.14 && _signal.CooldownMid <= 0.0)
                    {
                        SpawnPacket(PacketKind.MidRipple, effMid * 2.2 * ampBoost, baseVelocity);
                        _signal.CooldownMid = minCooldown * 0.85;
                    }

                    if ((effHigh - _signal.High) > 0.030 && effHigh > 0.14 && _signal.CooldownHigh <= 0.0)
                    {
                        SpawnPacket(PacketKind.HighNeedle, effHigh * 1.8 * ampBoost, baseVelocity);
                        _signal.CooldownHigh = minCooldown * 0.70;
                    }
                }

                _signal.SmoothToward(effBeat, effLow, effMid, effHigh, dt, 0.080, 0.120, 0.100, 0.070);
                _pulsePhase = (_pulsePhase + (BasePulseSpeed * pulseMultiplier * dt)) % Math.Tau;
            }
            else
            {
                _signal.Decay(dt, 0.140);
            }

            bool isDraining = _state.IsDraining && !_state.IsActive;
            if (isDraining)
                _drainOpacity = Math.Max(0f, _drainOpacity - (float)(dt * 2.8));
            else
                _drainOpacity = 1f;

            bool anyRemaining = false;
            for (int i = 0; i < _packets.Length; i++)
            {
                ref var p = ref _packets[i];
                if (!p.IsActive) continue;

                p.Position += p.Velocity * dt;
                p.Amplitude -= p.DecayRate * dt;

                if (p.Position - (p.Width * 0.5) > 1.05 || p.Amplitude <= 0.04)
                    p.IsActive = false;
                else
                    anyRemaining = true;
            }

            if (canRun)
            {
                PublishSharedWaveState(_state.TrackId, _packets, in _signal, _pulsePhase);
            }

            bool hasEnergy = _signal.HasEnergy(0.01);
            bool needsRedraw;
            bool continueAnim;

            if (_state.UseWave)
            {
                if (isDraining)
                {
                    continueAnim = (anyRemaining || hasEnergy) && _drainOpacity > 0.01f;
                    needsRedraw = true;
                }
                else
                {
                    needsRedraw = anyRemaining || hasEnergy || !_wasIdleFrame;
                    _wasIdleFrame = !anyRemaining && !hasEnergy;
                    continueAnim = anyRemaining || hasEnergy || (canRun && AudioVisualizerBuffer.HasRecentActivity);
                }
            }
            else
            {
                byte rawPulse = ComputePulseAlpha(_state.Color.Alpha, _state.IsSelected, _signal.Beat, _pulsePhase);
                byte scaledPulse = (byte)Math.Clamp((int)MathF.Round(rawPulse * _drainOpacity), 0, 255);
                if (isDraining)
                {
                    needsRedraw = scaledPulse != _lastPulseAlpha || _drainOpacity > 0.01f;
                    continueAnim = _drainOpacity > 0.01f;
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

        private void SpawnPacket(PacketKind kind, double rawAmp, double baseVelocity)
        {
            int targetIndex = -1;
            double minAmp = double.MaxValue;

            for (int i = 0; i < _packets.Length; i++)
            {
                if (!_packets[i].IsActive)
                {
                    targetIndex = i;
                    break;
                }

                if (_packets[i].Amplitude < minAmp)
                {
                    minAmp = _packets[i].Amplitude;
                    targetIndex = i;
                }
            }

            if (targetIndex < 0) return;

            ref var p = ref _packets[targetIndex];
            p.IsActive = true;

            int domBand = Math.Clamp(AudioVisualizerBuffer.DominantBandIndex, 0, 9);
            double bandRatio = domBand / 9.0;

            switch (kind)
            {
                case PacketKind.KickBullet:
                    p.Amplitude = Math.Clamp(rawAmp * _rng.NextDouble(0.92, 1.08), 1.6, 3.6);
                    p.Width = _rng.NextDouble(0.24, 0.32);
                    p.Velocity = baseVelocity * _rng.NextDouble(0.98, 1.04);
                    p.Frequency = 0.0;
                    p.PhaseOffset = 0.0;
                    p.HarmonicRatio = 0.0;
                    p.HarmonicPhase = 0.0;
                    p.DecayRate = _rng.NextDouble(0.09, 0.12);
                    break;

                case PacketKind.BassSwell:
                    p.Amplitude = Math.Clamp(rawAmp * _rng.NextDouble(0.90, 1.10), 1.3, 3.1);
                    p.Width = _rng.NextDouble(0.30, 0.42) * (1.05 - (0.15 * bandRatio));
                    p.Velocity = baseVelocity * _rng.NextDouble(0.84, 0.94);
                    p.Frequency = (2.4 + (domBand * 0.55)) * _rng.NextDouble(0.92, 1.08);
                    p.PhaseOffset = _rng.NextDouble(-Math.PI, Math.PI);
                    p.HarmonicRatio = _rng.NextDouble(0.15, 0.30);
                    p.HarmonicPhase = _rng.NextDouble(0.0, Math.Tau);
                    p.DecayRate = _rng.NextDouble(0.07, 0.10);
                    break;

                case PacketKind.MidRipple:
                    p.Amplitude = Math.Clamp(rawAmp * _rng.NextDouble(0.88, 1.12), 1.0, 2.5);
                    p.Width = _rng.NextDouble(0.18, 0.28);
                    p.Velocity = baseVelocity * _rng.NextDouble(1.00, 1.12);
                    p.Frequency = (4.2 + (domBand * 0.75)) * _rng.NextDouble(0.92, 1.08);
                    p.PhaseOffset = _rng.NextDouble(-Math.PI, Math.PI);
                    p.HarmonicRatio = _rng.NextDouble(0.10, 0.25);
                    p.HarmonicPhase = _rng.NextDouble(0.0, Math.Tau);
                    p.DecayRate = _rng.NextDouble(0.10, 0.14);
                    break;

                case PacketKind.HighNeedle:
                    p.Amplitude = Math.Clamp(rawAmp * _rng.NextDouble(0.85, 1.15), 0.8, 2.1);
                    p.Width = _rng.NextDouble(0.12, 0.18);
                    p.Velocity = baseVelocity * _rng.NextDouble(1.15, 1.28);
                    p.Frequency = (7.5 + (domBand * 0.65)) * _rng.NextDouble(0.92, 1.08);
                    p.PhaseOffset = _rng.NextDouble(-Math.PI, Math.PI);
                    p.HarmonicRatio = _rng.NextDouble(0.05, 0.20);
                    p.HarmonicPhase = _rng.NextDouble(0.0, Math.Tau);
                    p.DecayRate = _rng.NextDouble(0.13, 0.18);
                    break;
            }

            p.Position = -(p.Width * 0.5);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculateDisplacement(float u)
        {
            float rawDisp = 0f;

            for (int i = 0; i < _packets.Length; i++)
            {
                ref readonly var p = ref _packets[i];
                if (!p.IsActive) continue;

                float dist = u - (float)p.Position;
                float halfWidth = (float)p.Width * 0.5f;
                float absDist = MathF.Abs(dist);
                if (absDist >= halfWidth) continue;

                float norm = absDist / halfWidth;
                float invNorm2 = 1f - (norm * norm);
                float envelope = invNorm2 * invNorm2;

                float birthProgress = Math.Clamp(((float)p.Position + halfWidth) / ((float)p.Width * 0.8f), 0f, 1f);
                float birthFade = birthProgress * birthProgress * (3f - (2f * birthProgress));

                float exitProgress = Math.Clamp((1.02f - (float)p.Position) / ((float)p.Width * 0.8f), 0f, 1f);
                float exitFade = exitProgress * exitProgress * (3f - (2f * exitProgress));

                float packetValue;
                if (p.Frequency <= 0.01)
                {
                    packetValue = envelope * (float)p.Amplitude * birthFade * exitFade;
                }
                else
                {
                    float phase = (MathF.Tau * (float)p.Frequency * dist) + (float)p.PhaseOffset;
                    float carrier = MathF.Cos(phase) + ((float)p.HarmonicRatio * MathF.Cos((2f * phase) + (float)p.HarmonicPhase));
                    packetValue = envelope * (float)p.Amplitude * carrier * birthFade * exitFade;
                }

                rawDisp += packetValue;
            }

            if (MathF.Abs(rawDisp) < DisplacementZeroEpsilon) return 0f;

            float ratio = rawDisp / MaxSafeDisplacement;
            float safeDisp = rawDisp / MathF.Sqrt(1f + (ratio * ratio));

            float edgeTaper = 1f;
            if (u < CornerBlendMargin)
            {
                float t = u / CornerBlendMargin;
                edgeTaper = t * t * t * (t * ((t * 6f) - 15f) + 10f);
            }
            else if (u > (1f - CornerBlendMargin))
            {
                float t = (1f - u) / CornerBlendMargin;
                edgeTaper = t * t * t * (t * ((t * 6f) - 15f) + 10f);
            }

            return safeDisp * edgeTaper;
        }
    }

    #endregion

    #region Static Global Configuration

    private static bool _globalUseWave = true;
    private static TrackAnimationSpeed _globalAnimationSpeed = TrackAnimationSpeed.Medium;
    private static bool _isGlobalConfigLoaded;
    private static PlayerControlService? _globalPlayerControl;

    public static void ConfigureGlobal(bool useWave, TrackAnimationSpeed speed)
    {
        _globalUseWave = useWave;
        _globalAnimationSpeed = speed;
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
    private static readonly WavePacket[] s_sharedPackets = new WavePacket[MaxActivePackets];
    private static AudioReactiveSignalState s_sharedSignal;
    private static double s_sharedPulsePhase;
    private static string? s_sharedTrackId;
    private static long s_sharedTimestamp;
    private static bool s_hasSharedWave;

    private static void PublishSharedWaveState(
        string? trackId,
        WavePacket[] packets,
        in AudioReactiveSignalState signal,
        double pulsePhase)
    {
        lock (s_sharedWaveLock)
        {
            s_sharedTrackId = trackId;
            Array.Copy(packets, s_sharedPackets, MaxActivePackets);
            s_sharedSignal = signal;
            s_sharedPulsePhase = pulsePhase;
            s_sharedTimestamp = Stopwatch.GetTimestamp();
            s_hasSharedWave = true;
        }
    }

    private static void TryRestoreSharedWaveState(
        string? trackId,
        WavePacket[] targetPackets,
        ref AudioReactiveSignalState signal,
        ref double pulsePhase)
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
            if (elapsedSec > 3.5)
                return;

            Array.Copy(s_sharedPackets, targetPackets, MaxActivePackets);
            signal = s_sharedSignal;
            pulsePhase = s_sharedPulsePhase;
        }

        double dt = Math.Clamp(elapsedSec, 0.0, 0.8);
        pulsePhase = (pulsePhase + (BasePulseSpeed * dt)) % Math.Tau;

        for (int i = 0; i < MaxActivePackets; i++)
        {
            ref var p = ref targetPackets[i];
            if (!p.IsActive) continue;

            p.Position += p.Velocity * dt;
            if (p.Position - (p.Width * 0.5) > 1.05)
                p.IsActive = false;
        }
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
            WriteSnapshotLocked(seedChanged: true);
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