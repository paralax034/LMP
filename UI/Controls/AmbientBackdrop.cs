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
/// Render-thread-driven Skia ambient backdrop shared by Queue and Playlist screens.
/// Executes 60 FPS animations inside <see cref="CompositionCustomVisualHandler"/> directly on the render thread
/// (zero UI thread wakeups, zero compositor commit churn, zero <see cref="ImageBrush"/> FBO thrashing).
/// </summary>
public sealed class AmbientBackdrop : Panel
{
    private const float WashHeight = 320f;
    private const int FluidWidth = 112;
    private const int FluidMeshRows = 7;
    private const int FluidVertexCount = FluidWidth * FluidMeshRows;
    private const int MaxFluidPackets = 32;
    private const double MinFrameIntervalSeconds = 0.0125;
    private const double MaxFrameDeltaSeconds = 0.050;
    private const double DefaultHeaderHeight = 49.0;
    private const double NeutralBandWeight = 1.0 / 3.0;
    private const double BottomIntensityScale = 0.65;
    private const double MinTopPresenceFloor = 0.24;
    private const double CarrierWrapPeriod = Math.Tau * 200.0;

    private static readonly object MsgStop = new();
    private static readonly object MsgResumeFromSilence = new();
    private static readonly object MsgSyncState = new();

    private static readonly float[] FluidRowFractions = [0.00f, 0.07f, 0.14f, 0.30f, 0.52f, 0.76f, 1.00f];
    private static readonly ushort[] FluidMeshIndices = BuildFluidMeshIndices();

    private static ushort[] BuildFluidMeshIndices()
    {
        var indices = new ushort[(FluidWidth - 1) * (FluidMeshRows - 1) * 6];
        int ptr = 0;
        for (int r = 0; r < FluidMeshRows - 1; r++)
        {
            int rowStart = r * FluidWidth;
            int nextRowStart = (r + 1) * FluidWidth;
            for (int c = 0; c < FluidWidth - 1; c++)
            {
                ushort tl = (ushort)(rowStart + c);
                ushort tr = (ushort)(tl + 1);
                ushort bl = (ushort)(nextRowStart + c);
                ushort br = (ushort)(bl + 1);

                indices[ptr++] = tl;
                indices[ptr++] = bl;
                indices[ptr++] = tr;
                indices[ptr++] = tr;
                indices[ptr++] = bl;
                indices[ptr++] = br;
            }
        }
        return indices;
    }

    private struct FluidPacket
    {
        public double Position;
        public double Velocity;
        public double Amplitude;
        public double Width;
        public double DecayRate;
        public float AccentShift;
        public bool IsActive;
    }

    private struct BackdropStateSnapshot
    {
        public AmbientBackdropMode Mode;
        public double GlobalIntensity;
        public bool HasColor;
        public SKColor ColorLow;
        public SKColor ColorMid;
        public SKColor ColorHigh;
        public double VolumeFactor;
        public bool IsMixed;
        public double TopPresence;
        public double ScrollablePresence;
        public double ViewportTopOffset;
        public double BottomAnchorY;
        public bool IsBottomVisible;
        public bool IsPlayingReactive;
        public bool CanAnimate;
    }

    private sealed class AmbientWashHandler : CompositionCustomVisualHandler
    {
        private static readonly float[] WaveStops = [0f, 0.12f, 0.42f, 0.72f, 1.0f];

        [ThreadStatic] private static SKPaint? t_paint;
        [ThreadStatic] private static SKColor[]? t_gradColors;

        private readonly AmbientBackdrop _owner;
        private readonly bool _isBottom;
        private readonly FluidPacket[] _packets = new FluidPacket[MaxFluidPackets];
        private readonly SKPoint[] _meshPositions = new SKPoint[FluidVertexCount];
        private readonly SKColor[] _meshColors = new SKColor[FluidVertexCount];

        private BackdropStateSnapshot _state;
        private AudioReactiveSignalState _signal;
        private FastXorShift64 _rng;
        private double _lowWeight = NeutralBandWeight;
        private double _midWeight = NeutralBandWeight;
        private double _highWeight = NeutralBandWeight;
        private double _smoothedVolumeFactor = 1.0;
        private double _waveCarrierPhase;

        private SKShader? _cachedWaveShader;
        private uint _cachedWaveRgb;
        private SKVertices? _cachedFluidVertices;
        private float _cachedMeshWidth = -1f;
        private byte _cachedMeshAlpha;
        private bool _fluidMeshDirty = true;

        private SKColor _blendedRgb;
        private byte _masterAlpha;
        private float _scaleY = 1f;
        private bool _running;
        private bool _frameScheduled;
        private bool _hasValidContentBounds;
        private TimeSpan? _lastFrameTime;

        public AmbientWashHandler(AmbientBackdrop owner, bool isBottom)
        {
            _owner = owner;
            _isBottom = isBottom;
            _rng = new FastXorShift64((ulong)Stopwatch.GetTimestamp() ^ (isBottom ? 0x517CC1B727220A95UL : 0x9E3779B97F4A7C15UL));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ComputeMaxScaleY()
        {
            double excess = Math.Clamp(_state.VolumeFactor - 1.0, 0.0, 0.60);
            return _isBottom
                ? (float)(1.0 + (0.62 * excess))
                : (float)(1.0 + (0.72 * excess));
        }

        public override Rect GetRenderBounds()
        {
            double w = EffectiveSize.X;
            double h = EffectiveSize.Y;
            if (w <= 0.0 || h <= 0.0 || !_state.HasColor || _state.Mode == AmbientBackdropMode.Off || _masterAlpha == 0)
                return default;

            float maxScaleY = ComputeMaxScaleY();
            if (!_isBottom)
                return new Rect(0, 0, w, Math.Min(h, WashHeight * maxScaleY));

            if (!_state.IsBottomVisible)
                return default;

            double bottomSpatialY = _state.BottomAnchorY - (WashHeight * maxScaleY);
            double visTop = Math.Max(_state.ViewportTopOffset, bottomSpatialY);
            double visBottom = Math.Min(h, _state.BottomAnchorY);
            return visBottom > visTop ? new Rect(0, visTop, w, visBottom - visTop) : default;
        }

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, MsgStop))
            {
                _running = false;
                DisposeCachedResources();
                return;
            }

            if (ReferenceEquals(message, MsgResumeFromSilence))
            {
                if (_state.CanAnimate
                    && _state.IsPlayingReactive
                    && _state.HasColor
                    && _state.Mode != AmbientBackdropMode.Off
                    && (!_isBottom || _state.IsBottomVisible))
                {
                    if (!_running)
                    {
                        _running = true;
                        _lastFrameTime = null;
                        ScheduleNextFrame(forceRegister: true);
                    }
                }
                return;
            }

            if (ReferenceEquals(message, MsgSyncState))
            {
                var prevBounds = GetRenderBounds();
                SyncFromOwner();
                _fluidMeshDirty = true;

                if (!_state.HasColor || _state.Mode == AmbientBackdropMode.Off || (_isBottom && !_state.IsBottomVisible))
                {
                    _running = false;
                    if (!_state.HasColor || _state.Mode == AmbientBackdropMode.Off)
                    {
                        ResetEnvelopes();
                        DisposeCachedResources();
                    }
                    _masterAlpha = 0;
                    InvalidateDirtyUnion(prevBounds, default);
                    return;
                }

                if (!_state.CanAnimate)
                {
                    _running = false;
                    ResetEnvelopes();
                    RecomputeVisualParameters();
                    InvalidateDirtyUnion(prevBounds, GetRenderBounds());
                    return;
                }

                if (_state.Mode != AmbientBackdropMode.FluidWave)
                {
                    _cachedFluidVertices?.Dispose();
                    _cachedFluidVertices = null;
                }
                else
                {
                    _cachedWaveShader?.Dispose();
                    _cachedWaveShader = null;
                    _cachedWaveRgb = 0;
                }

                if (_state.IsPlayingReactive
                    && _state.Mode == AmbientBackdropMode.FluidWave
                    && !HasActivePackets()
                    && AudioVisualizerBuffer.HasRecentActivity)
                {
                    if (!TryRestoreSharedBackdropState(
                            _isBottom,
                            _packets,
                            ref _signal,
                            ref _lowWeight,
                            ref _midWeight,
                            ref _highWeight,
                            ref _waveCarrierPhase,
                            ref _rng))
                    {
                        SeedInitialLanterns();
                    }
                }

                if (!_state.IsPlayingReactive)
                    _smoothedVolumeFactor = Math.Clamp(_state.VolumeFactor, 1.0, 1.60);

                RecomputeVisualParameters();
                var nextBounds = GetRenderBounds();
                InvalidateDirtyUnion(prevBounds, nextBounds);

                if (_state.IsPlayingReactive || HasResidualState())
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
                    _signal.Reset();
                    _fluidMeshDirty = true;
                    RecomputeVisualParameters();
                    InvalidateDirtyUnion(nextBounds, GetRenderBounds());
                }
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            _frameScheduled = false;

            if (!_running || !_state.CanAnimate || !_state.HasColor || _state.Mode == AmbientBackdropMode.Off || (_isBottom && !_state.IsBottomVisible))
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

            var prevBounds = GetRenderBounds();
            byte prevAlpha = _masterAlpha;
            float prevScale = _scaleY;
            uint prevRgb = ToRgbKey(_blendedRgb);

            bool canRun = _state.CanAnimate && _state.IsPlayingReactive;
            bool fluidAnimated = TickFrame(dt, canRun);
            if (!_isBottom && canRun)
            {
                PublishSharedBackdropState(
                    _packets,
                    in _signal,
                    _lowWeight,
                    _midWeight,
                    _highWeight,
                    _waveCarrierPhase);
            }

            RecomputeVisualParameters();

            var nextBounds = GetRenderBounds();
            bool visualChanged = fluidAnimated
                              || _masterAlpha != prevAlpha
                              || MathF.Abs(_scaleY - prevScale) > 0.002f
                              || ToRgbKey(_blendedRgb) != prevRgb
                              || prevBounds != nextBounds;

            if (visualChanged)
            {
                if (fluidAnimated || ToRgbKey(_blendedRgb) != prevRgb)
                    _fluidMeshDirty = true;
                InvalidateDirtyUnion(prevBounds, nextBounds);
            }

            bool continueAnim = (canRun && AudioVisualizerBuffer.HasRecentActivity) || HasResidualState();
            if (continueAnim)
            {
                ScheduleNextFrame();
                return;
            }

            _running = false;
            if (!canRun)
            {
                _signal.Reset();
                _fluidMeshDirty = true;
                RecomputeVisualParameters();
                InvalidateDirtyUnion(nextBounds, GetRenderBounds());
            }
            else if (!_isBottom)
            {
                _owner._silenceMonitor.PostSilenceEntered();
            }
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (!_state.HasColor || _state.Mode == AmbientBackdropMode.Off || _masterAlpha == 0)
                return;

            if (_isBottom && !_state.IsBottomVisible)
                return;

            double w = EffectiveSize.X;
            double h = EffectiveSize.Y;
            if (w <= 1.0 || h <= 1.0)
                return;

            var leaseFeature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            byte effectiveAlpha = (byte)Math.Clamp((int)Math.Round(_masterAlpha * lease.CurrentOpacity), 0, 255);
            if (effectiveAlpha == 0) return;

            var canvas = lease.SkCanvas;
            var paint = t_paint ??= new SKPaint();
            paint.Reset();

            int saved = canvas.Save();
            try
            {
                if (_isBottom)
                {
                    canvas.ClipRect(new SKRect(0f, (float)_state.ViewportTopOffset, (float)w, (float)h), antialias: false);
                    canvas.Translate(0f, (float)_state.BottomAnchorY);
                    canvas.Scale(1f, -_scaleY);
                }
                else
                {
                    canvas.Scale(1f, _scaleY);
                }

                if (_state.Mode == AmbientBackdropMode.FluidWave)
                    DrawFluidWave(canvas, paint, (float)w, effectiveAlpha);
                else
                    DrawSmoothWash(canvas, paint, (float)w, effectiveAlpha);
            }
            finally
            {
                paint.Shader = null;
                paint.Reset();
                canvas.RestoreToCount(saved);
            }
        }

        private void SyncFromOwner()
        {
            lock (_owner._stateLock)
            {
                _state = _owner._snapshot;
                if (_isBottom)
                    _owner._bottomSyncQueued = false;
                else
                    _owner._topSyncQueued = false;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasArea(Rect r) => r.Width > 0.0 && r.Height > 0.0;

        private void InvalidateDirtyUnion(Rect prevBounds, Rect nextBounds)
        {
            bool hasPrev = HasArea(prevBounds);
            bool hasNext = HasArea(nextBounds);

            // ServerCompositionCustomVisual recomputes _ownContentBounds / _transformedSubTreeBounds
            // only via parameterless Invalidate() (InvalidateContent). Calling only Invalidate(Rect)
            // after initial null/zero bounds leaves _transformedSubTreeBounds == null and skips rendering.
            if (!_hasValidContentBounds || prevBounds != nextBounds)
            {
                _hasValidContentBounds = hasNext;
                if (hasPrev || hasNext)
                    Invalidate();
                return;
            }

            if (hasNext)
                Invalidate(nextBounds);
        }

        private bool HasActivePackets()
        {
            for (int i = 0; i < MaxFluidPackets; i++)
            {
                if (_packets[i].IsActive)
                    return true;
            }

            return false;
        }

        private void SeedInitialLanterns()
        {
            double[] initialPositions = _isBottom ? [0.14, 0.41, 0.68, 0.91] : [0.08, 0.34, 0.62, 0.86];
            float[] shifts = _isBottom ? [0.45f, -0.40f, 0.65f, -0.25f] : [0.60f, -0.45f, 0.30f, 0.80f];

            for (int i = 0; i < initialPositions.Length && i < MaxFluidPackets; i++)
            {
                double width = _rng.NextDouble(0.32, 0.48);
                _packets[i] = new FluidPacket
                {
                    IsActive = true,
                    Position = initialPositions[i],
                    Velocity = 0.40 * _rng.NextDouble(0.92, 1.08),
                    Amplitude = _rng.NextDouble(0.20, 0.32),
                    Width = width,
                    DecayRate = _rng.NextDouble(0.015, 0.028),
                    AccentShift = shifts[i]
                };
            }

            _waveCarrierPhase = _isBottom ? 2.1 : 0.0;
        }

        private bool HasResidualState()
        {
            if (_signal.HasEnergy(0.01))
                return true;

            if (_state.Mode == AmbientBackdropMode.FluidWave && HasActivePackets())
                return true;

            return false;
        }

        private void ResetEnvelopes()
        {
            _signal.Reset();
            _lowWeight = NeutralBandWeight;
            _midWeight = NeutralBandWeight;
            _highWeight = NeutralBandWeight;
            _smoothedVolumeFactor = Math.Clamp(_state.VolumeFactor, 1.0, 1.60);
            for (int i = 0; i < MaxFluidPackets; i++)
                _packets[i].IsActive = false;
        }

        private bool TickFrame(double dt, bool canRun)
        {
            double weightAlpha = 1.0 - Math.Exp(-dt / 0.240);
            double targetVolume = Math.Clamp(_state.VolumeFactor, 1.0, 1.60);
            _smoothedVolumeFactor += (targetVolume - _smoothedVolumeFactor) * (1.0 - Math.Exp(-dt / 0.160));

            bool fluidAnimated = false;

            if (canRun)
            {
                double beat = AudioVisualizerBuffer.BeatPulse;
                double low = AudioVisualizerBuffer.LowLevel;
                double mid = AudioVisualizerBuffer.MidLevel;
                double high = AudioVisualizerBuffer.HighLevel;

                if (_state.Mode == AmbientBackdropMode.FluidWave)
                {
                    double excess = Math.Clamp(_smoothedVolumeFactor - 1.0, 0.0, 0.60);
                    double baseVelocity = 0.40 * (1.0 + (0.28 * excess));
                    double ampBoost = 1.0 + (0.45 * excess);

                    _signal.AdvanceCooldowns(dt);

                    if (beat > 0.20 && beat > _signal.Beat && _signal.CooldownBeat <= 0.0)
                    {
                        SpawnPacket(beat * 0.42 * ampBoost, baseVelocity, _rng.NextDouble(0.36, 0.50), accentShift: 0.65f);
                        _signal.CooldownBeat = 0.46;
                    }

                    if ((low - _signal.Low) > 0.035 && low > 0.14 && _signal.CooldownLow <= 0.0)
                    {
                        SpawnPacket(low * 0.35 * ampBoost, baseVelocity * 0.88, _rng.NextDouble(0.44, 0.60), accentShift: -0.55f);
                        _signal.CooldownLow = 0.58;
                    }

                    if ((mid - _signal.Mid) > 0.040 && mid > 0.15 && _signal.CooldownMid <= 0.0)
                    {
                        SpawnPacket(mid * 0.28 * ampBoost, baseVelocity * 1.05, _rng.NextDouble(0.30, 0.44), accentShift: 0.25f);
                        _signal.CooldownMid = 0.48;
                    }

                    if ((high - _signal.High) > 0.042 && high > 0.15 && _signal.CooldownHigh <= 0.0)
                    {
                        SpawnPacket(high * 0.24 * ampBoost, baseVelocity * 1.18, _rng.NextDouble(0.22, 0.34), accentShift: 0.90f);
                        _signal.CooldownHigh = 0.40;
                    }

                    _waveCarrierPhase = (_waveCarrierPhase + (dt * (0.85 + (0.45 * _signal.Beat)))) % CarrierWrapPeriod;
                    fluidAnimated = true;
                }

                _signal.SmoothToward(beat, low, mid, high, dt, 0.095, 0.135, 0.120, 0.095);

                double total = _signal.Low + _signal.Mid + _signal.High + 0.09;
                _lowWeight += (((_signal.Low + 0.03) / total) - _lowWeight) * weightAlpha;
                _midWeight += (((_signal.Mid + 0.03) / total) - _midWeight) * weightAlpha;
                _highWeight += (((_signal.High + 0.03) / total) - _highWeight) * weightAlpha;
            }
            else
            {
                _signal.Decay(dt, 0.200);
                _lowWeight += (NeutralBandWeight - _lowWeight) * weightAlpha;
                _midWeight += (NeutralBandWeight - _midWeight) * weightAlpha;
                _highWeight += (NeutralBandWeight - _highWeight) * weightAlpha;
            }

            if (_state.Mode == AmbientBackdropMode.FluidWave)
            {
                for (int i = 0; i < MaxFluidPackets; i++)
                {
                    ref var p = ref _packets[i];
                    if (!p.IsActive) continue;

                    p.Position += p.Velocity * dt;
                    p.Amplitude = Math.Max(0.08, p.Amplitude - (p.DecayRate * dt));

                    if (p.Position - (p.Width * 0.5) > 1.04)
                        p.IsActive = false;
                    else
                        fluidAnimated = true;
                }
            }

            return fluidAnimated;
        }

        private void SpawnPacket(double amplitude, double velocity, double width, float accentShift)
        {
            int targetIndex = -1;

            for (int i = 0; i < MaxFluidPackets; i++)
            {
                if (!_packets[i].IsActive)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                return;

            ref var p = ref _packets[targetIndex];
            p.IsActive = true;
            p.Position = -(width * 0.5);
            p.Velocity = velocity * _rng.NextDouble(0.92, 1.08);
            p.Amplitude = Math.Clamp(amplitude, 0.16, 0.50);
            p.Width = width;
            p.DecayRate = _rng.NextDouble(0.015, 0.028);
            p.AccentShift = accentShift;
        }

        private void RecomputeVisualParameters()
        {
            if (!_state.HasColor || _state.Mode == AmbientBackdropMode.Off)
            {
                _masterAlpha = 0;
                return;
            }

            byte r = (byte)Math.Clamp((int)MathF.Round((_state.ColorLow.Red * (float)_lowWeight) + (_state.ColorMid.Red * (float)_midWeight) + (_state.ColorHigh.Red * (float)_highWeight)), 0, 255);
            byte g = (byte)Math.Clamp((int)MathF.Round((_state.ColorLow.Green * (float)_lowWeight) + (_state.ColorMid.Green * (float)_midWeight) + (_state.ColorHigh.Green * (float)_highWeight)), 0, 255);
            byte b = (byte)Math.Clamp((int)MathF.Round((_state.ColorLow.Blue * (float)_lowWeight) + (_state.ColorMid.Blue * (float)_midWeight) + (_state.ColorHigh.Blue * (float)_highWeight)), 0, 255);
            _blendedRgb = new SKColor(r, g, b);

            double adaptiveLumScale = ComputeAdaptiveBrightnessScale(Color.FromRgb(r, g, b));
            double excess = Math.Clamp(_smoothedVolumeFactor - 1.0, 0.0, 0.60);
            double volumeGain = 0.80 + (0.62 * excess);
            double beatWeight = 0.34 + (0.30 * excess);
            double purityScale = _state.IsMixed ? 0.65 : 1.0;
            double energyScale = purityScale * _state.GlobalIntensity * adaptiveLumScale * volumeGain
                               * (0.78 + (beatWeight * _signal.Beat) + (0.16 * (_signal.Low + _signal.Mid)));

            double masterScale = _state.Mode == AmbientBackdropMode.FluidWave ? 255.0 : 102.0;
            double presence = _isBottom ? (_state.ScrollablePresence * BottomIntensityScale) : _state.TopPresence;

            _masterAlpha = (byte)Math.Clamp((int)Math.Round(masterScale * Math.Clamp(presence * energyScale, 0.0, 1.0)), 0, 255);
            _scaleY = _isBottom
                ? (float)(1.0 + (0.50 * excess) + (0.12 * _signal.Beat * excess))
                : (float)(1.0 + (0.58 * excess) + (0.14 * _signal.Beat * excess));
        }

        private void DisposeCachedResources()
        {
            _cachedWaveShader?.Dispose();
            _cachedWaveShader = null;
            _cachedWaveRgb = 0;

            _cachedFluidVertices?.Dispose();
            _cachedFluidVertices = null;
            _cachedMeshWidth = -1f;
            _fluidMeshDirty = true;
        }

        private void DrawSmoothWash(SKCanvas canvas, SKPaint paint, float width, byte effectiveAlpha)
        {
            uint rgbKey = ToQuantizedRgbKey(_blendedRgb);
            if (_cachedWaveShader == null || _cachedWaveRgb != rgbKey)
            {
                _cachedWaveShader?.Dispose();
                var colors = t_gradColors ??= new SKColor[5];
                byte r = _blendedRgb.Red;
                byte g = _blendedRgb.Green;
                byte b = _blendedRgb.Blue;
                colors[0] = new SKColor(r, g, b, 132);
                colors[1] = new SKColor(r, g, b, 200);
                colors[2] = new SKColor(r, g, b, 82);
                colors[3] = new SKColor(r, g, b, 22);
                colors[4] = new SKColor(r, g, b, 0);

                _cachedWaveShader = SKShader.CreateLinearGradient(
                    new SKPoint(0f, 0f),
                    new SKPoint(0f, WashHeight),
                    colors,
                    WaveStops,
                    SKShaderTileMode.Clamp);
                _cachedWaveRgb = rgbKey;
            }

            paint.IsAntialias = false;
            paint.IsDither = true;
            paint.Shader = _cachedWaveShader;
            paint.Color = new SKColor(255, 255, 255, effectiveAlpha);
            canvas.DrawRect(0f, 0f, width, WashHeight, paint);
        }

        private void DrawFluidWave(SKCanvas canvas, SKPaint paint, float width, byte effectiveAlpha)
        {
            if (_cachedFluidVertices == null
                || _fluidMeshDirty
                || MathF.Abs(_cachedMeshWidth - width) > 0.5f
                || _cachedMeshAlpha != effectiveAlpha)
            {
                RebuildFluidVertices(width, effectiveAlpha);
            }

            if (_cachedFluidVertices == null)
                return;

            paint.IsAntialias = false;
            paint.IsDither = true;
            paint.Shader = null;
            paint.Color = SKColors.White;
            canvas.DrawVertices(_cachedFluidVertices, SKBlendMode.Dst, paint);
        }

        private void RebuildFluidVertices(float width, byte effectiveAlpha)
        {
            float baseR = _blendedRgb.Red;
            float baseG = _blendedRgb.Green;
            float baseB = _blendedRgb.Blue;
            float highDeltaR = _state.ColorHigh.Red - baseR;
            float highDeltaG = _state.ColorHigh.Green - baseG;
            float highDeltaB = _state.ColorHigh.Blue - baseB;
            float lowDeltaR = _state.ColorLow.Red - baseR;
            float lowDeltaG = _state.ColorLow.Green - baseG;
            float lowDeltaB = _state.ColorLow.Blue - baseB;

            Span<float> colReach = stackalloc float[FluidWidth];
            Span<float> colShift = stackalloc float[FluidWidth];
            Span<float> colGlow = stackalloc float[FluidWidth];

            const float invW = 1f / (FluidWidth - 1);
            for (int x = 0; x < FluidWidth; x++)
            {
                float u = x * invW;
                float carrier = (0.045f * MathF.Sin((u * MathF.Tau * 1.5f) + (float)_waveCarrierPhase))
                              + (0.025f * MathF.Cos((u * MathF.Tau * 2.5f) - ((float)_waveCarrierPhase * 0.7f)));

                float packetSum = 0f;
                float shiftSum = 0f;

                for (int i = 0; i < MaxFluidPackets; i++)
                {
                    ref readonly var p = ref _packets[i];
                    if (!p.IsActive) continue;

                    float halfW = (float)p.Width * 0.5f;
                    float absDist = MathF.Abs(u - (float)p.Position);
                    if (absDist >= halfW) continue;

                    float norm = absDist / halfW;
                    float invNorm2 = 1f - (norm * norm);
                    float env = invNorm2 * invNorm2;

                    float birthProgress = Math.Clamp(((float)p.Position + halfW) / ((float)p.Width * 0.65f), 0f, 1f);
                    float birthFade = birthProgress * birthProgress * (3f - (2f * birthProgress));

                    float contrib = env * (float)p.Amplitude * birthFade;
                    packetSum += contrib;
                    shiftSum += contrib * p.AccentShift;
                }

                float combined = 0.62f + carrier + (packetSum / MathF.Sqrt(1f + (packetSum * packetSum * 2.4f)));
                colReach[x] = Math.Clamp(combined, 0.48f, 0.95f);
                colShift[x] = Math.Clamp(shiftSum, -0.80f, 0.80f);
                colGlow[x] = Math.Clamp(packetSum, 0f, 1.15f);
            }

            const float maxAlpha = 140f;
            float alphaNorm = effectiveAlpha / 255f;

            for (int r = 0; r < FluidMeshRows; r++)
            {
                float frac = FluidRowFractions[r];
                int rowOffset = r * FluidWidth;

                for (int x = 0; x < FluidWidth; x++)
                {
                    float reach = colReach[x];
                    float yNorm = frac * reach;
                    float xPx = x * invW * width;
                    float yPx = yNorm * WashHeight;
                    _meshPositions[rowOffset + x] = new SKPoint(xPx, yPx);

                    if (frac >= 0.999f)
                    {
                        _meshColors[rowOffset + x] = new SKColor((byte)baseR, (byte)baseG, (byte)baseB, 0);
                        continue;
                    }

                    float topJoin = yNorm < 0.14f
                        ? 0.72f + (0.28f * SmoothStepF(yNorm / 0.14f))
                        : 1.0f;
                    float v = 1f - frac;
                    float smoothFall = v * v * (3f - (2f * v));
                    float rawShift = colShift[x];
                    float glow = colGlow[x];

                    float baseAlpha = maxAlpha * topJoin * smoothFall;
                    if (glow > 0f)
                        baseAlpha *= 1f + (0.34f * glow * (1f - (0.30f * v)));

                    float satLift = 1f + (0.16f * glow * (1f - (0.30f * v)));
                    float rCol = baseR * satLift;
                    float gCol = baseG * satLift;
                    float bCol = baseB * satLift;

                    if (rawShift != 0f)
                    {
                        baseAlpha *= 1f + (0.16f * MathF.Abs(rawShift) * (1f - v));
                        float shift = rawShift * (1f - (0.45f * v));
                        if (shift > 0f)
                        {
                            rCol += highDeltaR * shift;
                            gCol += highDeltaG * shift;
                            bCol += highDeltaB * shift;
                        }
                        else
                        {
                            float neg = -shift;
                            rCol += lowDeltaR * neg;
                            gCol += lowDeltaG * neg;
                            bCol += lowDeltaB * neg;
                        }
                    }

                    byte a = (byte)Math.Clamp((int)MathF.Round(baseAlpha * alphaNorm), 0, 255);
                    byte rByte = (byte)Math.Clamp((int)MathF.Round(rCol), 0, 255);
                    byte gByte = (byte)Math.Clamp((int)MathF.Round(gCol), 0, 255);
                    byte bByte = (byte)Math.Clamp((int)MathF.Round(bCol), 0, 255);
                    _meshColors[rowOffset + x] = new SKColor(rByte, gByte, bByte, a);
                }
            }

            _cachedFluidVertices?.Dispose();
            _cachedFluidVertices = SKVertices.CreateCopy(SKVertexMode.Triangles, _meshPositions, null, _meshColors, FluidMeshIndices);
            _cachedMeshWidth = width;
            _cachedMeshAlpha = effectiveAlpha;
            _fluidMeshDirty = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ToQuantizedRgbKey(SKColor c) =>
            (((uint)c.Red & 0xFC) << 16) | (((uint)c.Green & 0xFC) << 8) | ((uint)c.Blue & 0xFC);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ToRgbKey(SKColor c) =>
            ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SmoothStepF(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return t * t * (3f - (2f * t));
        }
    }

    private static readonly Lock s_sharedStateLock = new();
    private static readonly FluidPacket[] s_sharedPackets = new FluidPacket[MaxFluidPackets];
    private static AudioReactiveSignalState s_sharedSignal;
    private static double s_sharedLowWeight = NeutralBandWeight;
    private static double s_sharedMidWeight = NeutralBandWeight;
    private static double s_sharedHighWeight = NeutralBandWeight;
    private static double s_sharedCarrierPhase;
    private static long s_sharedTimestamp;
    private static bool s_hasSharedState;

    private static void PublishSharedBackdropState(
        FluidPacket[] packets,
        in AudioReactiveSignalState signal,
        double lowWeight,
        double midWeight,
        double highWeight,
        double carrierPhase)
    {
        lock (s_sharedStateLock)
        {
            Array.Copy(packets, s_sharedPackets, MaxFluidPackets);
            s_sharedSignal = signal;
            s_sharedLowWeight = lowWeight;
            s_sharedMidWeight = midWeight;
            s_sharedHighWeight = highWeight;
            s_sharedCarrierPhase = carrierPhase;
            s_sharedTimestamp = Stopwatch.GetTimestamp();
            s_hasSharedState = true;
        }
    }

    private static bool TryRestoreSharedBackdropState(
        bool isBottom,
        FluidPacket[] targetPackets,
        ref AudioReactiveSignalState signal,
        ref double lowWeight,
        ref double midWeight,
        ref double highWeight,
        ref double carrierPhase,
        ref FastXorShift64 rng)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec;

        lock (s_sharedStateLock)
        {
            if (!s_hasSharedState)
                return false;

            elapsedSec = Stopwatch.GetElapsedTime(s_sharedTimestamp, now).TotalSeconds;
            if (elapsedSec > 4.0)
                return false;

            Array.Copy(s_sharedPackets, targetPackets, MaxFluidPackets);
            signal = s_sharedSignal;
            lowWeight = s_sharedLowWeight;
            midWeight = s_sharedMidWeight;
            highWeight = s_sharedHighWeight;
            carrierPhase = s_sharedCarrierPhase;
        }

        double dt = Math.Clamp(elapsedSec, 0.0, 1.0);
        carrierPhase = (carrierPhase + (dt * 0.90) + (isBottom ? 2.15 : 0.0)) % CarrierWrapPeriod;

        for (int i = 0; i < MaxFluidPackets; i++)
        {
            ref var p = ref targetPackets[i];
            if (!p.IsActive) continue;

            p.Position += p.Velocity * dt;
            if (p.Position - (p.Width * 0.5) > 1.04)
            {
                p.IsActive = false;
                continue;
            }

            if (isBottom)
            {
                double minPos = -(p.Width * 0.5);
                double span = 1.04 + p.Width;
                double norm = (p.Position - minPos) / span;
                norm = (norm + 0.37 + (i * 0.031)) % 1.0;
                if (norm < 0.0) norm += 1.0;

                p.Position = minPos + (norm * span);
                p.Velocity *= rng.NextDouble(0.90, 1.12);
                p.Width = Math.Clamp(p.Width * rng.NextDouble(0.92, 1.10), 0.22, 0.62);
            }
        }

        return true;
    }

    private static AmbientBackdropMode _globalMode = AmbientBackdropMode.FluidWave;
    private static double _globalIntensity = 1.0;
    private static bool _isGlobalConfigLoaded;

    internal static AmbientBackdropMode CurrentMode
    {
        get
        {
            EnsureGlobalConfigLoaded();
            return _globalMode;
        }
    }

    internal static double CurrentIntensity
    {
        get
        {
            EnsureGlobalConfigLoaded();
            return _globalIntensity;
        }
    }

    public static void ConfigureGlobal(AmbientBackdropMode mode, int intensityPercent)
    {
        _globalMode = mode;
        _globalIntensity = Math.Clamp(intensityPercent, 10, 200) / 100.0;
        _isGlobalConfigLoaded = true;
        GlobalConfigChanged?.Invoke();
    }

    internal static event Action? GlobalConfigChanged;

    private static void EnsureGlobalConfigLoaded()
    {
        if (_isGlobalConfigLoaded) return;

        try
        {
            var settings = AppEntry.Services.GetService<LibraryService>()?.Settings;
            if (settings != null)
            {
                _globalMode = settings.AmbientBackdropMode;
                _globalIntensity = Math.Clamp(settings.AmbientBackdropIntensity, 10, 200) / 100.0;
                _isGlobalConfigLoaded = true;
            }
        }
        catch
        {
            // Designer guard
        }
    }

    private readonly Action _globalConfigChangedHandler;
    private readonly EventHandler<AvaloniaPropertyChangedEventArgs> _scrollViewerChangedHandler;
    private readonly CompositionSilenceMonitor _silenceMonitor;
    private readonly Control _topLayer;
    private readonly Control _bottomLayer;
    private readonly Lock _stateLock = new();

    private CompositionCustomVisual? _topVisual;
    private CompositionCustomVisual? _bottomVisual;
    private BackdropStateSnapshot _snapshot;
    private bool _topSyncQueued;
    private bool _bottomSyncQueued;

    private ScrollViewer? _boundScrollViewer;
    private double _viewportTopOffset = DefaultHeaderHeight;
    private double _scrollExtentHeight;
    private double _scrollMaxOffsetY;
    private double _scrollOffsetY;
    private bool _showBottomWash = true;
    private double _effectiveTopFactor = 1.0;

    private Color? _cachedBaseColor;
    private SKColor _colorLow;
    private SKColor _colorMid;
    private SKColor _colorHigh;
    private bool _isAttached;

    public static readonly StyledProperty<Color?> BaseColorProperty =
        AvaloniaProperty.Register<AmbientBackdrop, Color?>(nameof(BaseColor));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AmbientBackdrop, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<bool> IsReactiveProperty =
        AvaloniaProperty.Register<AmbientBackdrop, bool>(nameof(IsReactive), true);

    public static readonly StyledProperty<double> VolumeFactorProperty =
        AvaloniaProperty.Register<AmbientBackdrop, double>(nameof(VolumeFactor), 1.0);

    public static readonly StyledProperty<bool> IsMixedProperty =
        AvaloniaProperty.Register<AmbientBackdrop, bool>(nameof(IsMixed));

    public Color? BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsReactive
    {
        get => GetValue(IsReactiveProperty);
        set => SetValue(IsReactiveProperty, value);
    }

    public double VolumeFactor
    {
        get => GetValue(VolumeFactorProperty);
        set => SetValue(VolumeFactorProperty, value);
    }

    public bool IsMixed
    {
        get => GetValue(IsMixedProperty);
        set => SetValue(IsMixedProperty, value);
    }

    internal double EffectiveTopFactor => _effectiveTopFactor;

    internal event Action<double>? TopGlowFactorChanged;

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        _globalConfigChangedHandler = OnGlobalConfigChanged;
        _scrollViewerChangedHandler = OnScrollViewerPropertyChanged;
        _silenceMonitor = new CompositionSilenceMonitor(
            this,
            shouldAnimate: () => BaseColor.HasValue && _globalMode != AmbientBackdropMode.Off,
            onStateReevaluated: PushStateToHandlers,
            onResumeFromSilence: () =>
            {
                _topVisual?.SendHandlerMessage(MsgResumeFromSilence);
                _bottomVisual?.SendHandlerMessage(MsgResumeFromSilence);
            },
            shouldPollSilence: () => BaseColor.HasValue && _globalMode != AmbientBackdropMode.Off && IsPlaying && IsReactive);

        _topLayer = new Control { IsHitTestVisible = false, ClipToBounds = false };
        _bottomLayer = new Control { IsHitTestVisible = false, ClipToBounds = false };
        _topLayer.AttachedToVisualTree += OnTopLayerAttachedToVisualTree;
        _bottomLayer.AttachedToVisualTree += OnBottomLayerAttachedToVisualTree;

        Children.Add(_topLayer);
        Children.Add(_bottomLayer);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i].Measure(availableSize);

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        for (int i = 0; i < Children.Count; i++)
            Children[i].Arrange(rect);

        var size = new Vector(finalSize.Width, finalSize.Height);
        bool visualChanged = EnsureTopVisualCore(size) | EnsureBottomVisualCore(size);

        if (_topVisual != null && _topVisual.Size != size)
        {
            _topVisual.Size = size;
            visualChanged = true;
        }

        if (_bottomVisual != null && _bottomVisual.Size != size)
        {
            _bottomVisual.Size = size;
            visualChanged = true;
        }

        if (visualChanged)
            PushStateToHandlers();

        return finalSize;
    }

    internal void AttachScrollViewer(ScrollViewer? scrollViewer)
    {
        if (ReferenceEquals(_boundScrollViewer, scrollViewer))
        {
            SyncFromScrollViewer();
            return;
        }

        DetachScrollViewer();

        _boundScrollViewer = scrollViewer;
        if (_boundScrollViewer != null)
        {
            _boundScrollViewer.PropertyChanged += _scrollViewerChangedHandler;
            SyncFromScrollViewer();
        }
    }

    internal void DetachScrollViewer()
    {
        if (_boundScrollViewer != null)
        {
            _boundScrollViewer.PropertyChanged -= _scrollViewerChangedHandler;
            _boundScrollViewer = null;
        }
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty
            || e.Property == ScrollViewer.ExtentProperty
            || e.Property == ScrollViewer.ViewportProperty
            || e.Property == BoundsProperty
            || e.Property == IsVisibleProperty)
        {
            SyncFromScrollViewer();
        }
    }

    private void SyncFromScrollViewer()
    {
        if (_boundScrollViewer == null)
            return;

        double maxOffsetY = Math.Max(0.0, _boundScrollViewer.Extent.Height - _boundScrollViewer.Viewport.Height);
        UpdateScrollMetrics(
            viewportTopOffset: _boundScrollViewer.Bounds.Y,
            extentHeight: _boundScrollViewer.Extent.Height,
            maxOffsetY: maxOffsetY,
            offsetY: _boundScrollViewer.Offset.Y,
            showBottomWash: _boundScrollViewer.IsVisible);
    }

    internal void UpdateScrollMetrics(
        double viewportTopOffset,
        double extentHeight,
        double maxOffsetY,
        double offsetY,
        bool showBottomWash)
    {
        double normalizedTop = viewportTopOffset > 0.0 ? viewportTopOffset : DefaultHeaderHeight;
        double normalizedExtent = Math.Max(0.0, extentHeight);
        double normalizedMax = Math.Max(0.0, maxOffsetY);
        double normalizedOffset = Math.Max(0.0, offsetY);

        if (Math.Abs(_viewportTopOffset - normalizedTop) < 0.01
            && Math.Abs(_scrollExtentHeight - normalizedExtent) < 0.01
            && Math.Abs(_scrollMaxOffsetY - normalizedMax) < 0.01
            && Math.Abs(_scrollOffsetY - normalizedOffset) < 0.01
            && _showBottomWash == showBottomWash)
        {
            return;
        }

        _viewportTopOffset = normalizedTop;
        _scrollExtentHeight = normalizedExtent;
        _scrollMaxOffsetY = normalizedMax;
        _scrollOffsetY = normalizedOffset;
        _showBottomWash = showBottomWash;

        UpdateEffectiveTopFactor();

        if (!BaseColor.HasValue || _globalMode == AmbientBackdropMode.Off)
            return;

        PushStateToHandlers();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;

        EnsureGlobalConfigLoaded();
        GlobalConfigChanged += _globalConfigChangedHandler;
        _silenceMonitor.Attach();

        RebuildPalette(BaseColor);
        UpdateEffectiveTopFactor();

        var size = new Vector(Bounds.Width, Bounds.Height);
        EnsureTopVisualCore(size);
        EnsureBottomVisualCore(size);
        PushStateToHandlers();
    }

    private void OnTopLayerAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_isAttached && EnsureTopVisualCore(new Vector(Bounds.Width, Bounds.Height)))
            PushStateToHandlers();
    }

    private void OnBottomLayerAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_isAttached && EnsureBottomVisualCore(new Vector(Bounds.Width, Bounds.Height)))
            PushStateToHandlers();
    }

    private bool EnsureTopVisualCore(Vector size)
    {
        if (!_isAttached || _topVisual != null)
            return false;

        if (ElementComposition.GetElementVisual(_topLayer)?.Compositor is not { } compositor)
            return false;

        lock (_stateLock)
        {
            _topSyncQueued = false;
        }

        _topVisual = compositor.CreateCustomVisual(new AmbientWashHandler(this, isBottom: false));
        _topVisual.Size = size;
        ElementComposition.SetElementChildVisual(_topLayer, _topVisual);
        return true;
    }

    private bool EnsureBottomVisualCore(Vector size)
    {
        if (!_isAttached || _bottomVisual != null)
            return false;

        if (ElementComposition.GetElementVisual(_bottomLayer)?.Compositor is not { } compositor)
            return false;

        lock (_stateLock)
        {
            _bottomSyncQueued = false;
        }

        _bottomVisual = compositor.CreateCustomVisual(new AmbientWashHandler(this, isBottom: true));
        _bottomVisual.Size = size;
        ElementComposition.SetElementChildVisual(_bottomLayer, _bottomVisual);
        return true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        GlobalConfigChanged -= _globalConfigChangedHandler;
        DetachScrollViewer();
        _silenceMonitor.Detach();

        if (_topVisual != null)
        {
            _topVisual.SendHandlerMessage(MsgStop);
            ElementComposition.SetElementChildVisual(_topLayer, null);
            _topVisual = null;
        }

        if (_bottomVisual != null)
        {
            _bottomVisual.SendHandlerMessage(MsgStop);
            ElementComposition.SetElementChildVisual(_bottomLayer, null);
            _bottomVisual = null;
        }

        lock (_stateLock)
        {
            _topSyncQueued = false;
            _bottomSyncQueued = false;
        }

        _cachedBaseColor = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BaseColorProperty)
        {
            RebuildPalette(change.GetNewValue<Color?>());
            PushStateToHandlers();
        }
        else if (change.Property == IsPlayingProperty
              || change.Property == IsReactiveProperty
              || change.Property == VolumeFactorProperty)
        {
            PushStateToHandlers();
        }
        else if (change.Property == IsMixedProperty)
        {
            UpdateEffectiveTopFactor();
            PushStateToHandlers();
        }
    }

    private void OnGlobalConfigChanged()
    {
        RebuildPalette(BaseColor);
        PushStateToHandlers();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private double ComputeTopPresence()
    {
        if (!_showBottomWash || _scrollMaxOffsetY <= 40.0)
            return 1.0;

        double scrollProgress = Math.Clamp(_scrollOffsetY / _scrollMaxOffsetY, 0.0, 1.0);
        double gentleDeparture = 1.0 - (0.28 * SmoothStep(_scrollOffsetY / 900.0));
        double bottomFade = 1.0 - (0.68 * SmoothStep((scrollProgress - 0.20) / 0.80));
        return Math.Max(MinTopPresenceFloor, gentleDeparture * bottomFade);
    }

    private bool ComputeBottomVisible(double height, double volumeFactor)
    {
        if (!BaseColor.HasValue || _globalMode == AmbientBackdropMode.Off)
            return false;

        if (!_showBottomWash || _scrollMaxOffsetY <= 40.0 || height <= _viewportTopOffset + 40.0)
            return false;

        double excess = Math.Clamp(volumeFactor - 1.0, 0.0, 0.60);
        double maxBottomScaleY = 1.0 + (0.64 * excess);
        double bottomAnchorY = _viewportTopOffset + _scrollExtentHeight - _scrollOffsetY;
        double bottomSpatialY = bottomAnchorY - (WashHeight * maxBottomScaleY);
        return bottomSpatialY < height && bottomAnchorY > _viewportTopOffset;
    }

    private void UpdateEffectiveTopFactor()
    {
        double factor = ComputeTopPresence() * (IsMixed ? 0.65 : 1.0);
        if (Math.Abs(_effectiveTopFactor - factor) >= 0.01)
        {
            _effectiveTopFactor = factor;
            TopGlowFactorChanged?.Invoke(factor);
        }
    }

    private void PushStateToHandlers()
    {
        if (!_isAttached) return;

        var size = new Vector(Bounds.Width, Bounds.Height);
        EnsureTopVisualCore(size);
        EnsureBottomVisualCore(size);

        double vol = Math.Clamp(VolumeFactor, 1.0, 1.60);
        bool bottomVisible = ComputeBottomVisible(size.Y, vol);
        bool canAnimate = _silenceMonitor.CanAnimateNow;

        if (!canAnimate || !IsPlaying || !IsReactive)
            _silenceMonitor.StopIdleTimer();

        bool sendTop = false;
        bool sendBottom = false;

        lock (_stateLock)
        {
            _snapshot = new BackdropStateSnapshot
            {
                Mode = _globalMode,
                GlobalIntensity = _globalIntensity,
                HasColor = BaseColor.HasValue,
                ColorLow = _colorLow,
                ColorMid = _colorMid,
                ColorHigh = _colorHigh,
                VolumeFactor = vol,
                IsMixed = IsMixed,
                TopPresence = ComputeTopPresence(),
                ScrollablePresence = _showBottomWash ? SmoothStep((_scrollMaxOffsetY - 40.0) / 120.0) : 0.0,
                ViewportTopOffset = _viewportTopOffset,
                BottomAnchorY = _viewportTopOffset + _scrollExtentHeight - _scrollOffsetY,
                IsBottomVisible = bottomVisible,
                IsPlayingReactive = IsReactive && IsPlaying,
                CanAnimate = canAnimate
            };

            if (_topVisual != null && !_topSyncQueued)
            {
                _topSyncQueued = true;
                sendTop = true;
            }

            if (_bottomVisual != null && !_bottomSyncQueued)
            {
                _bottomSyncQueued = true;
                sendBottom = true;
            }
        }

        if (sendTop)
            _topVisual?.SendHandlerMessage(MsgSyncState);

        if (sendBottom)
            _bottomVisual?.SendHandlerMessage(MsgSyncState);
    }

    private void RebuildPalette(Color? baseColor)
    {
        EnsureGlobalConfigLoaded();

        if (!baseColor.HasValue || _globalMode == AmbientBackdropMode.Off)
        {
            _cachedBaseColor = null;
            return;
        }

        if (_cachedBaseColor == baseColor.Value)
            return;

        _cachedBaseColor = baseColor.Value;

        var baseLifted = LiftAmbientColor(baseColor.Value);
        var low = ShiftColor(baseLifted, hueDeltaDegrees: -10f, lightScale: 0.90f, lightClampMin: 0.28f, lightClampMax: 0.50f);
        var high = ShiftColor(baseLifted, hueDeltaDegrees: 12f, lightScale: 1.08f, lightClampMin: 0.34f, lightClampMax: 0.58f);

        _colorLow = new SKColor(low.R, low.G, low.B);
        _colorMid = new SKColor(baseLifted.R, baseLifted.G, baseLifted.B);
        _colorHigh = new SKColor(high.R, high.G, high.B);
    }

    internal static Color LiftAmbientColor(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max <= 0)
            return Color.FromRgb(108, 108, 118);

        const int minGlowChannel = 128;
        if (max >= minGlowChannel)
            return Color.FromRgb(c.R, c.G, c.B);

        double scale = (double)minGlowChannel / max;
        return Color.FromRgb(
            (byte)Math.Min(255, Math.Round(c.R * scale)),
            (byte)Math.Min(255, Math.Round(c.G * scale)),
            (byte)Math.Min(255, Math.Round(c.B * scale)));
    }

    internal static double ComputeAdaptiveBrightnessScale(Color c)
    {
        static double Linearize(byte ch)
        {
            double s = ch / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        double lum = (0.2126 * Linearize(c.R)) + (0.7152 * Linearize(c.G)) + (0.0722 * Linearize(c.B));
        if (lum <= 0.22)
            return 1.0;

        return Math.Clamp(Math.Sqrt(0.22 / lum), 0.42, 1.0);
    }

    private static Color ShiftColor(Color c, float hueDeltaDegrees, float lightScale, float lightClampMin, float lightClampMax)
    {
        float r = c.R / 255f;
        float g = c.G / 255f;
        float b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        float l = (max + min) * 0.5f;
        float s = delta < 0.001f ? 0f : (l > 0.5f ? delta / (2f - max - min) : delta / (max + min));

        if (s < 0.08f)
        {
            byte gray = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(l * lightScale, 0.30f, 0.72f) * 255f), 0, 255);
            return Color.FromRgb(gray, gray, gray);
        }

        float h = max == r
            ? ((g - b) / delta + (g < b ? 6f : 0f)) / 6f
            : max == g
                ? ((b - r) / delta + 2f) / 6f
                : ((r - g) / delta + 4f) / 6f;

        h = (h + (hueDeltaDegrees / 360f)) % 1f;
        if (h < 0f) h += 1f;
        s = Math.Clamp(s, 0.22f, 0.82f);
        l = Math.Clamp(l * lightScale, lightClampMin, lightClampMax);

        float q = l < 0.5f ? l * (1f + s) : l + s - (l * s);
        float p = (2f * l) - q;
        return Color.FromRgb(
            HueToByte(p, q, h + (1f / 3f)),
            HueToByte(p, q, h),
            HueToByte(p, q, h - (1f / 3f)));
    }

    private static byte HueToByte(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;

        float val = t < 1f / 6f ? p + ((q - p) * 6f * t)
            : t < 0.5f ? q
            : t < 2f / 3f ? p + ((q - p) * ((2f / 3f) - t) * 6f)
            : p;

        return (byte)Math.Clamp((int)MathF.Round(val * 255f), 0, 255);
    }
}