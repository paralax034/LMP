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
/// Render-thread-driven Skia GPU ambient backdrop shared by Queue and Playlist screens.
/// Executes 60 FPS animations inside <see cref="CompositionCustomVisualHandler"/> using two compiled SkSL
/// <see cref="SKRuntimeEffect"/> fragment shaders (<see cref="AmbientBackdropMode.FluidWave"/> left-to-right stream
/// and <see cref="AmbientBackdropMode.Aurora"/> organic curtain) with zero UI wakeups, zero CPU vertex rebuilds,
/// and seamless render-thread synchronization with <see cref="AmbientTitleBarBleed"/> across the window header seam.
/// </summary>
public sealed class AmbientBackdrop : Panel
{
    private const float WashHeight = 360f;
    internal const int MaxFluidWavePackets = 12;
    private const int MaxAuroraRipples = 4;
    private const float FluidAttackSeconds = 0.085f;
    private const double MinFrameIntervalSeconds = 0.030;
    private const double MaxFrameDeltaSeconds = 0.050;
    private const double DefaultHeaderHeight = 49.0;
    private const double NeutralBandWeight = 1.0 / 3.0;
    private const double BottomIntensityScale = 0.68;
    private const double CarrierWrapPeriod = Math.Tau * 200.0;
    private const double ShaderMasterScale = 196.0;
    private const double FallbackMasterScale = 104.0;
    internal const int ShaderTexWidth = 256;
    private const int ShaderTexHeight = 64;
    internal const int TitleBarTexHeight = 16;
    internal static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static readonly object MsgStop = new();
    private static readonly object MsgResumeFromSilence = new();
    private static readonly object MsgSyncState = new();

    // SkSL GPU fragment shader for FluidWave (default) — shared by Page wash (uParams[0].z < 0.5)
    // and TitleBar upward continuation (uParams[0].z >= 0.5) for pixel-exact seam continuity at y=0.
    private const string FluidWaveSksl = """
        uniform float4 uColors[3];
        uniform float4 uParams[3];
        uniform float4 uWaves[12];

        float2 evalPacket(float u, float phase, float4 w) {
            float dx = (u - w.x) * w.y;
            float shaped = dx > 0.0 ? dx * 1.22 : dx * 0.86;
            float env = exp(-(shaped * shaped) * 1.75) * w.z;
            float ripple = 1.0 + 0.15 * cos(dx * 3.14159 - phase * 0.45) * clamp(abs(w.w) + 0.25, 0.0, 1.0);
            float e = env * ripple;
            return float2(e, e * w.w);
        }

        half4 main(float2 coord) {
            float2 size = uParams[0].xy;
            float isTitleBar = uParams[0].z;
            float react = uParams[0].w;
            float u = clamp(coord.x / max(size.x, 1.0), 0.0, 1.0);
            float vRaw = clamp(coord.y / max(size.y, 1.0), 0.0, 1.0);
            float v = isTitleBar > 0.5 ? 0.0 : vRaw;

            float macro = uParams[1].x;
            float kick = uParams[1].y;
            float vocal = uParams[1].z;
            float drop = uParams[1].w;

            float phase = uParams[2].x;
            float centroid = uParams[2].y;
            float shimmer = uParams[2].z;
            float harmonic = uParams[2].w;

            // Left-to-right traveling base carrier waves (-phase ensures rightward motion)
            float flowA = sin(u * 6.2831853 * 1.40 - phase) * 0.050;
            float flowB = cos(u * 6.2831853 * 2.55 - phase * 1.28) * 0.028;
            float flowC = sin(u * 6.2831853 * 4.30 - phase * 1.90) * (0.012 + shimmer * 0.028);
            float streamSwell = (flowA + flowB) * (0.60 + harmonic * 0.70 + macro * 0.40) + flowC;

            // Unrolled evaluation of 12 left-to-right traveling packets (ES2-safe, zero dynamic indexing)
            float2 p0 = evalPacket(u, phase, uWaves[0]);
            float2 p1 = evalPacket(u, phase, uWaves[1]);
            float2 p2 = evalPacket(u, phase, uWaves[2]);
            float2 p3 = evalPacket(u, phase, uWaves[3]);
            float2 p4 = evalPacket(u, phase, uWaves[4]);
            float2 p5 = evalPacket(u, phase, uWaves[5]);
            float2 p6 = evalPacket(u, phase, uWaves[6]);
            float2 p7 = evalPacket(u, phase, uWaves[7]);
            float2 p8 = evalPacket(u, phase, uWaves[8]);
            float2 p9 = evalPacket(u, phase, uWaves[9]);
            float2 p10 = evalPacket(u, phase, uWaves[10]);
            float2 p11 = evalPacket(u, phase, uWaves[11]);

            float rawPacketSum = p0.x + p1.x + p2.x + p3.x + p4.x + p5.x + p6.x + p7.x + p8.x + p9.x + p10.x + p11.x;
            float rawTint = p0.y + p1.y + p2.y + p3.y + p4.y + p5.y + p6.y + p7.y + p8.y + p9.y + p10.y + p11.y;

            // Smooth C-infinity soft saturation prevents flat pegging on heavy-bass tracks
            float packetSum = 1.35 * (1.0 - exp(-rawPacketSum * 0.76));
            float tintSum = rawTint / (1.0 + abs(rawTint) * 0.85);

            // Flowing vocal glow drifting gently along the stream
            float vocalWave = (0.65 + 0.35 * sin(u * 6.2831853 * 1.15 - phase * 0.55)) * vocal;

            // Balanced vertical reach: always gracefully present (>= 0.48), expands organically with reactivity
            float reactGain = 0.38 + 0.62 * react;
            float baseReach = 0.50 + (clamp(macro * 0.24, 0.0, 0.32) + kick * 0.11 + drop * 0.11) * reactGain;
            float reach = clamp(baseReach + streamSwell + packetSum * 0.19 + vocalWave * 0.12, 0.42, 0.98);

            float yRel = clamp(v / reach, 0.0, 1.0);
            float invY = 1.0 - yRel;
            float falloff = invY * invY * (3.0 - 2.0 * invY);

            // Golden-middle seam: TitleBar stays calmer (0.16 -> 0.72) for crisp tab contrast,
            // Page continues smoothly from 0.72 at v=0 to 1.00 -> seamless without washing out chrome.
            float verticalMask = isTitleBar > 0.5
                ? mix(0.16, 0.72, smoothstep(0.0, 1.0, vRaw))
                : falloff * (smoothstep(0.0, 0.06, v) * 0.28 + 0.72);

            // Pure palette color blending with crossmodal tint shift from passing packets
            float3 col = uColors[1].rgb;
            float lowMix = clamp((1.0 - centroid) * 0.44 + kick * 0.18 + max(0.0, -tintSum) * 0.52, 0.0, 0.80);
            float highMix = clamp(centroid * 0.46 + shimmer * 0.32 + max(0.0, tintSum) * 0.58, 0.0, 0.84);
            col = mix(col, uColors[0].rgb, lowMix * (0.52 + 0.48 * yRel));
            col = mix(col, uColors[2].rgb, highMix * (0.45 + 0.55 * invY));

            float topCrest = exp(-v * 7.2) * (shimmer * 0.36 + kick * 0.22 + drop * 0.26 + packetSum * 0.18) * reactGain;
            float glowBoost = (vocalWave * 0.26 * invY + packetSum * 0.22 * (0.35 + 0.65 * invY) + topCrest);
            col = clamp(col * (1.0 + glowBoost * 0.30) + float3(0.12, 0.12, 0.14) * (topCrest * 0.42 + drop * 0.14 * invY), 0.0, 1.0);

            float alpha = (0.70 + glowBoost * 0.26) * verticalMask;
            alpha = clamp(alpha, 0.0, 0.88);

            return half4(col * alpha, alpha);
        }
        """;

    // SkSL GPU fragment shader for Aurora — shared by Page wash (uParams[0].z < 0.5)
    // and TitleBar upward continuation (uParams[0].z >= 0.5) for pixel-exact seam continuity at y=0.
    private const string AuroraSksl = """
        uniform float4 uColors[3];
        uniform float4 uParams[3];
        uniform float4 uWaves[4];

        float2 evalRipple(float u, float4 w) {
            float d = abs(abs(u - w.x) - w.y);
            float bw = max(0.06, 0.19 - w.y * 0.06);
            float e = exp(-(d * d) / (bw * bw)) * w.z;
            return float2(e, e * w.w);
        }

        half4 main(float2 coord) {
            float2 size = uParams[0].xy;
            float isTitleBar = uParams[0].z;
            float react = uParams[0].w;
            float u = clamp(coord.x / max(size.x, 1.0), 0.0, 1.0);
            float vRaw = clamp(coord.y / max(size.y, 1.0), 0.0, 1.0);
            float v = isTitleBar > 0.5 ? 0.0 : vRaw;

            float macro = uParams[1].x;
            float kick = uParams[1].y;
            float vocal = uParams[1].z;
            float drop = uParams[1].w;

            float phase = uParams[2].x;
            float centroid = uParams[2].y;
            float shimmer = uParams[2].z;
            float harmonic = uParams[2].w;

            float waveA = sin(u * 6.2831853 * 1.35 + phase) * 0.052;
            float waveB = cos(u * 6.2831853 * 2.40 - phase * 0.72) * 0.030;
            float waveC = sin(u * 6.2831853 * 4.10 + phase * 1.35) * (0.013 + shimmer * 0.030);
            float harmonicSwell = (waveA + waveB) * (0.65 + harmonic * 0.72 + macro * 0.42) + waveC;

            float2 r0 = evalRipple(u, uWaves[0]);
            float2 r1 = evalRipple(u, uWaves[1]);
            float2 r2 = evalRipple(u, uWaves[2]);
            float2 r3 = evalRipple(u, uWaves[3]);
            float rippleSum = r0.x + r1.x + r2.x + r3.x;
            float tintSum = clamp(r0.y + r1.y + r2.y + r3.y, -0.85, 0.85);

            float centerDist = abs(u - (0.50 + 0.07 * sin(phase * 0.37)));
            float vocalBell = exp(-(centerDist * centerDist) / 0.115) * vocal;

            float reactGain = 0.38 + 0.62 * react;
            float baseReach = 0.52 + (clamp(macro * 0.24, 0.0, 0.32) + kick * 0.11 + drop * 0.11) * reactGain;
            float reach = clamp(baseReach + harmonicSwell + rippleSum * 0.20 + vocalBell * 0.14, 0.44, 0.98);

            float yRel = clamp(v / reach, 0.0, 1.0);
            float invY = 1.0 - yRel;
            float falloff = invY * invY * (3.0 - 2.0 * invY);

            float verticalMask = isTitleBar > 0.5
                ? mix(0.16, 0.72, smoothstep(0.0, 1.0, vRaw))
                : falloff * (smoothstep(0.0, 0.06, v) * 0.28 + 0.72);

            float3 col = uColors[1].rgb;
            float lowMix = clamp((1.0 - centroid) * 0.44 + kick * 0.20 - tintSum * 0.34, 0.0, 0.78);
            float highMix = clamp(centroid * 0.46 + shimmer * 0.33 + max(0.0, tintSum) * 0.54, 0.0, 0.84);
            col = mix(col, uColors[0].rgb, lowMix * (0.55 + 0.45 * yRel));
            col = mix(col, uColors[2].rgb, highMix * (0.45 + 0.55 * invY));

            float topCrest = exp(-v * 7.5) * (shimmer * 0.38 + kick * 0.24 + drop * 0.28) * reactGain;
            float glowBoost = vocalBell * 0.30 * invY + rippleSum * 0.22 * (0.4 + 0.6 * invY) + topCrest;
            col = clamp(col * (1.0 + glowBoost * 0.30) + float3(0.12, 0.12, 0.14) * (topCrest * 0.44 + drop * 0.15 * invY), 0.0, 1.0);

            float alpha = (0.70 + glowBoost * 0.26) * verticalMask;
            alpha = clamp(alpha, 0.0, 0.88);

            return half4(col * alpha, alpha);
        }
        """;

    private static readonly SKRuntimeEffect? s_fluidWaveEffect = CompileEffect(FluidWaveSksl, "FluidWave");
    private static readonly SKRuntimeEffect? s_auroraEffect = CompileEffect(AuroraSksl, "Aurora");

    internal static bool IsFluidWaveShaderCompiled => s_fluidWaveEffect != null;
    internal static bool IsAuroraShaderCompiled => s_auroraEffect != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SKRuntimeEffect? ResolveEffectForMode(AmbientBackdropMode mode) => mode switch
    {
        AmbientBackdropMode.FluidWave => s_fluidWaveEffect,
        AmbientBackdropMode.Aurora => s_auroraEffect,
        _ => null
    };

    private static SKRuntimeEffect? CompileEffect(string sksl, string label)
    {
        try
        {
            var effect = SKRuntimeEffect.CreateShader(sksl, out string? errors);
            if (effect == null && !string.IsNullOrEmpty(errors))
                Log.Warn($"[AmbientBackdrop] SkSL {label} shader compile failed, falling back to smooth wash: {errors}");
            return effect;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AmbientBackdrop] SKRuntimeEffect unavailable for {label}, falling back to smooth wash: {ex.Message}");
            return null;
        }
    }

    #region Render-Thread Live Seam Bus (Zero-Alloc TitleBar Continuation)

    internal interface ILiveSeamListener
    {
        void OnLiveSeamUpdated();
    }

    private static readonly Lock s_liveSeamLock = new();
    private static readonly float[] s_seamColors = new float[12];
    private static readonly float[] s_seamParams = new float[12];
    private static readonly float[] s_seamWavesFluid = new float[MaxFluidWavePackets * 4];
    private static readonly float[] s_seamWavesAurora = new float[MaxAuroraRipples * 4];
    private static ILiveSeamListener? s_titleBarListener;
    private static AmbientBackdropMode s_seamMode = AmbientBackdropMode.FluidWave;
    private static object? s_seamOwner;
    private static byte s_seamMasterAlpha;
    private static SKColor s_seamBlendedRgb;
    private static uint s_seamColorMidKey;
    private static bool s_seamHasData;
    private static long s_seamFrameSeq;

    internal static void RegisterTitleBarListener(ILiveSeamListener listener)
    {
        lock (s_liveSeamLock)
        {
            s_titleBarListener = listener;
        }
    }

    internal static void UnregisterTitleBarListener(ILiveSeamListener listener)
    {
        lock (s_liveSeamLock)
        {
            if (ReferenceEquals(s_titleBarListener, listener))
                s_titleBarListener = null;
        }
    }

    private static void ClearLiveSeamIfOwner(object owner)
    {
        ILiveSeamListener? listener = null;

        lock (s_liveSeamLock)
        {
            if (!ReferenceEquals(s_seamOwner, owner))
                return;

            s_seamOwner = null;
            if (s_seamHasData)
            {
                s_seamHasData = false;
                s_seamMasterAlpha = 0;
                s_seamFrameSeq++;
                listener = s_titleBarListener;
            }
        }

        // Direct server-side render-thread callback (never calls UI-thread SendHandlerMessage from render thread)
        listener?.OnLiveSeamUpdated();
    }

    private static void PublishLiveSeamState(
        object owner,
        AmbientBackdropMode mode,
        uint colorMidKey,
        byte masterAlpha,
        SKColor blendedRgb,
        bool isAnimating,
        float[] uColors,
        float[] uParams,
        float[] uWavesFluid,
        float[] uWavesAurora)
    {
        _ = isAnimating;
        ILiveSeamListener? listener;

        lock (s_liveSeamLock)
        {
            s_seamOwner = owner;
            s_seamMode = mode;
            s_seamColorMidKey = colorMidKey;
            s_seamMasterAlpha = masterAlpha;
            s_seamBlendedRgb = blendedRgb;
            s_seamHasData = true;

            Array.Copy(uColors, s_seamColors, 12);
            Array.Copy(uParams, s_seamParams, 12);
            if (mode == AmbientBackdropMode.FluidWave)
                Array.Copy(uWavesFluid, s_seamWavesFluid, MaxFluidWavePackets * 4);
            else
                Array.Copy(uWavesAurora, s_seamWavesAurora, MaxAuroraRipples * 4);

            s_seamFrameSeq++;
            listener = s_titleBarListener;
        }

        // Synchronous render-thread notification in the exact same compositor commit (0 ms lag, 0 UI wakeups)
        listener?.OnLiveSeamUpdated();
    }

    internal static bool TryReadLiveSeamState(
        uint expectedColorMidKey,
        ref long lastSeq,
        out AmbientBackdropMode mode,
        out byte masterAlpha,
        out SKColor blendedRgb,
        float[] destColors,
        float[] destParams,
        float[] destWavesFluid,
        float[] destWavesAurora)
    {
        lock (s_liveSeamLock)
        {
            if (!s_seamHasData || s_seamColorMidKey != expectedColorMidKey)
            {
                mode = _globalMode;
                masterAlpha = 0;
                blendedRgb = default;
                return false;
            }

            mode = s_seamMode;
            masterAlpha = s_seamMasterAlpha;
            blendedRgb = s_seamBlendedRgb;

            if (s_seamFrameSeq != lastSeq)
            {
                lastSeq = s_seamFrameSeq;
                Array.Copy(s_seamColors, destColors, 12);
                Array.Copy(s_seamParams, destParams, 12);
                if (mode == AmbientBackdropMode.FluidWave)
                    Array.Copy(s_seamWavesFluid, destWavesFluid, MaxFluidWavePackets * 4);
                else
                    Array.Copy(s_seamWavesAurora, destWavesAurora, MaxAuroraRipples * 4);
            }

            return true;
        }
    }

    #endregion

    /// <summary>
    /// Shared wave slot used as either a left-to-right traveling packet (<see cref="AmbientBackdropMode.FluidWave"/>, up to 12)
    /// or a bilateral radial shockwave (<see cref="AmbientBackdropMode.Aurora"/>, up to 4).
    /// </summary>
    internal struct WaveSlot
    {
        public float Position; // FluidWave: X [0..1.7], Aurora: center [0.15..0.85]
        public float Param;    // FluidWave: half-width, Aurora: radius
        public float Speed;
        public float Amplitude;
        public float Age;
        public float DecayRate;
        public float PauseFade;
        public float Tint;
        public bool IsActive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothStepF(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    /// <summary>
    /// Spawns a left-to-right FluidWave packet without ever hard-evicting a visible mid-screen wave.
    /// </summary>
    internal static void SpawnFluidPacketCore(
        Span<WaveSlot> waves,
        float halfWidth,
        float speed,
        float amplitude,
        float tint)
    {
        int freeSlot = -1;
        int mergeEntrySlot = -1;
        int limit = Math.Min(waves.Length, MaxFluidWavePackets);

        for (int i = 0; i < limit; i++)
        {
            if (!waves[i].IsActive)
            {
                freeSlot = i;
                break;
            }

            if (mergeEntrySlot < 0 && waves[i].Position < 0.12f)
                mergeEntrySlot = i;
        }

        if (freeSlot < 0)
        {
            // All 12 slots visibly active: smoothly reinforce a fresh left-entry wave instead of popping one mid-screen
            if (mergeEntrySlot >= 0)
            {
                ref var existing = ref waves[mergeEntrySlot];
                existing.Amplitude = MathF.Min(1.25f, MathF.Max(existing.Amplitude, amplitude) + (amplitude * 0.20f));
                existing.Tint = (existing.Tint * 0.65f) + (tint * 0.35f);
                existing.PauseFade = 1f;
            }
            return;
        }

        ref var w = ref waves[freeSlot];
        w.IsActive = true;
        w.Position = 0.01f;
        w.Param = halfWidth;
        w.Speed = speed;
        w.Amplitude = amplitude;
        w.Age = 0f;
        w.DecayRate = 0.14f;
        w.PauseFade = 1f;
        w.Tint = tint;
    }

    /// <summary>
    /// Computes the smooth effective amplitude of a FluidWave packet across its full left-to-right journey.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputeFluidEffectiveAmplitude(in WaveSlot w)
    {
        if (!w.IsActive) return 0f;

        float attack = SmoothStepF(w.Age * (1f / FluidAttackSeconds));
        float travelDamp = MathF.Exp(-w.Age * w.DecayRate);
        float exitStart = 1.0f - (w.Param * 0.35f);
        float exitSpan = MathF.Max(0.10f, w.Param * 2.55f);
        float exitFade = 1f - SmoothStepF((w.Position - exitStart) / exitSpan);

        return w.Amplitude * attack * travelDamp * exitFade * w.PauseFade;
    }

    /// <summary>
    /// Advances all active FluidWave packets smoothly across the full screen width, deactivating only once
    /// the packet has completely dissolved off the right edge or faded out on pause.
    /// </summary>
    internal static bool StepFluidPacketsCore(Span<WaveSlot> waves, float fdt, bool isPlaying)
    {
        bool anyActive = false;
        float pauseDecay = isPlaying ? 1f : MathF.Exp(-fdt / 0.45f);
        int limit = Math.Min(waves.Length, MaxFluidWavePackets);

        for (int i = 0; i < limit; i++)
        {
            ref var w = ref waves[i];
            if (!w.IsActive) continue;

            w.Age += fdt;
            w.Position += w.Speed * fdt;
            if (!isPlaying)
                w.PauseFade *= pauseDecay;

            float exitCompletePos = 1.0f + (w.Param * 2.25f);
            if (w.Position >= exitCompletePos || w.PauseFade < 0.004f)
            {
                w.IsActive = false;
                continue;
            }

            anyActive = true;
        }

        return anyActive;
    }

    private struct BackdropStateSnapshot
    {
        public AmbientBackdropMode Mode;
        public double GlobalIntensity;
        public double Reactivity;
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
        [ThreadStatic] private static SKPaint? t_texPaint;
        [ThreadStatic] private static SKColor[]? t_gradColors;

        private readonly AmbientBackdrop _owner;
        private readonly bool _isBottom;
        private readonly WaveSlot[] _waves = new WaveSlot[MaxFluidWavePackets];

        // Preallocated packed uniform arrays (0 B per-frame uniform array allocations)
        private readonly float[] _uColors = new float[12];
        private readonly float[] _uParams = new float[12];
        private readonly float[] _uWavesFluid = new float[MaxFluidWavePackets * 4];
        private readonly float[] _uWavesAurora = new float[MaxAuroraRipples * 4];

        private BackdropStateSnapshot _state;
        private AudioReactiveSignalState _signal;
        private FastXorShift64 _rng;

        // 2nd-order damped spring physics & balanced perceptual envelopes
        private double _macroEnergy;
        private double _kickSpringPos;
        private double _kickSpringVel;
        private double _vocalAura;
        private double _dropBloom;
        private double _shimmerEnergy;
        private double _harmonicEnergy;
        private double _playingBlend;
        private double _centroid = 0.40;
        private double _lowWeight = NeutralBandWeight;
        private double _midWeight = NeutralBandWeight;
        private double _highWeight = NeutralBandWeight;
        private double _smoothedVolumeFactor = 1.0;
        private double _waveCarrierPhase;

        private SKRuntimeEffect? _boundEffect;
        private SKRuntimeEffectUniforms? _shaderUniforms;
        private SKShader? _cachedRuntimeShader;
        private SKSurface? _shaderSurface;
        private GRContext? _surfaceGrContext;
        private SKImage? _cachedFrameImage;
        private bool _shaderDirty = true;
        private bool _uniformsPacked;

        private SKShader? _cachedFallbackShader;
        private uint _cachedFallbackRgb;

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
                ? (float)(1.22 + (0.35 * excess))
                : (float)(1.34 + (0.40 * excess));
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
                if (!_isBottom)
                    ClearLiveSeamIfOwner(this);
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
                var prevMode = _state.Mode;
                uint prevLow = ToRgbKey(_state.ColorLow);
                uint prevMid = ToRgbKey(_state.ColorMid);
                uint prevHigh = ToRgbKey(_state.ColorHigh);
                bool prevPlaying = _state.IsPlayingReactive;
                double prevReact = _state.Reactivity;

                SyncFromOwner();

                if (prevMode != _state.Mode)
                {
                    ClearWaveSlots();
                    DisposeRuntimeShader();
                    _shaderDirty = true;
                }
                else if (prevLow != ToRgbKey(_state.ColorLow)
                      || prevMid != ToRgbKey(_state.ColorMid)
                      || prevHigh != ToRgbKey(_state.ColorHigh)
                      || prevPlaying != _state.IsPlayingReactive
                      || Math.Abs(prevReact - _state.Reactivity) > 0.005)
                {
                    _shaderDirty = true;
                }

                if (!_state.HasColor || _state.Mode == AmbientBackdropMode.Off || (_isBottom && !_state.IsBottomVisible))
                {
                    _running = false;
                    if (!_isBottom && (!_state.HasColor || _state.Mode == AmbientBackdropMode.Off))
                        ClearLiveSeamIfOwner(this);
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
                    _shaderDirty = true;
                    RecomputeVisualParameters();
                    PublishTopSeamIfOwner(isAnimating: false);
                    InvalidateDirtyUnion(prevBounds, GetRenderBounds());
                    return;
                }

                if (_state.IsPlayingReactive
                    && !HasActiveWaves()
                    && AudioVisualizerBuffer.HasRecentActivity)
                {
                    TryRestoreSharedBackdropState(
                        _state.Mode,
                        _isBottom,
                        _waves,
                        ref _signal,
                        ref _macroEnergy,
                        ref _vocalAura,
                        ref _waveCarrierPhase);
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
                    PublishTopSeamIfOwner(isAnimating: true);
                }
                else
                {
                    _running = false;
                    _signal.Reset();
                    _shaderDirty = true;
                    RecomputeVisualParameters();
                    PublishTopSeamIfOwner(isAnimating: false);
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
                PublishTopSeamIfOwner(isAnimating: false);
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
            bool shaderAnimated = TickFrame(dt, canRun);
            if (!_isBottom && canRun)
            {
                PublishSharedBackdropState(
                    _state.Mode,
                    _waves,
                    in _signal,
                    _macroEnergy,
                    _vocalAura,
                    _waveCarrierPhase);
            }

            RecomputeVisualParameters();

            var nextBounds = GetRenderBounds();
            bool visualChanged = shaderAnimated
                              || _masterAlpha != prevAlpha
                              || MathF.Abs(_scaleY - prevScale) > 0.002f
                              || ToRgbKey(_blendedRgb) != prevRgb
                              || prevBounds != nextBounds;

            bool continueAnim = (canRun && AudioVisualizerBuffer.HasRecentActivity) || HasResidualState();

            if (visualChanged)
            {
                if (shaderAnimated || ToRgbKey(_blendedRgb) != prevRgb)
                    _shaderDirty = true;
                PublishTopSeamIfOwner(isAnimating: continueAnim);
                InvalidateDirtyUnion(prevBounds, nextBounds);
            }

            if (continueAnim)
            {
                ScheduleNextFrame();
                return;
            }

            _running = false;
            if (!canRun)
            {
                ResetEnvelopes();
                _shaderDirty = true;
                RecomputeVisualParameters();
                PublishTopSeamIfOwner(isAnimating: false);
                InvalidateDirtyUnion(nextBounds, GetRenderBounds());
            }
            else if (!_isBottom)
            {
                PublishTopSeamIfOwner(isAnimating: false);
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

                var effect = ResolveEffectForMode(_state.Mode);
                if (effect != null)
                    DrawRuntimeShader(canvas, lease.GrContext, paint, effect, (float)w, effectiveAlpha);
                else
                    DrawSmoothWashFallback(canvas, paint, (float)w, effectiveAlpha);
            }
            finally
            {
                paint.Shader = null;
                paint.Reset();
                canvas.RestoreToCount(saved);
            }
        }

        private void PublishTopSeamIfOwner(bool isAnimating)
        {
            if (_isBottom)
                return;

            if (!_state.HasColor || !_state.CanAnimate || _state.Mode == AmbientBackdropMode.Off)
            {
                ClearLiveSeamIfOwner(this);
                return;
            }

            PackUniformArrays();
            _uniformsPacked = true;
            PublishLiveSeamState(
                this,
                _state.Mode,
                ToRgbKey(_state.ColorMid),
                _masterAlpha,
                _blendedRgb,
                isAnimating,
                _uColors,
                _uParams,
                _uWavesFluid,
                _uWavesAurora);
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

        private void ClearWaveSlots() => Array.Clear(_waves);

        private bool HasActiveWaves()
        {
            int count = _state.Mode == AmbientBackdropMode.FluidWave ? MaxFluidWavePackets : MaxAuroraRipples;
            for (int i = 0; i < count; i++)
            {
                if (_waves[i].IsActive)
                    return true;
            }
            return false;
        }

        private bool HasResidualState()
        {
            double targetPlayBlend = _state.IsPlayingReactive ? 1.0 : 0.0;
            if (Math.Abs(_playingBlend - targetPlayBlend) > 0.01)
                return true;

            if (_signal.HasEnergy(0.008)
                || _macroEnergy > 0.015
                || Math.Abs(_kickSpringPos) > 0.008
                || Math.Abs(_kickSpringVel) > 0.04
                || _vocalAura > 0.012
                || _dropBloom > 0.012
                || _shimmerEnergy > 0.012
                || _harmonicEnergy > 0.012)
            {
                return true;
            }

            return HasActiveWaves();
        }

        private void ResetEnvelopes()
        {
            _signal.Reset();
            _macroEnergy = 0.0;
            _kickSpringPos = 0.0;
            _kickSpringVel = 0.0;
            _vocalAura = 0.0;
            _dropBloom = 0.0;
            _shimmerEnergy = 0.0;
            _harmonicEnergy = 0.0;
            _playingBlend = _state.IsPlayingReactive ? 1.0 : 0.0;
            _centroid = 0.40;
            _lowWeight = NeutralBandWeight;
            _midWeight = NeutralBandWeight;
            _highWeight = NeutralBandWeight;
            _smoothedVolumeFactor = Math.Clamp(_state.VolumeFactor, 1.0, 1.60);
            ClearWaveSlots();
        }

        private bool TickFrame(double dt, bool canRun)
        {
            double weightAlpha = 1.0 - Math.Exp(-dt / 0.200);
            double targetVolume = Math.Clamp(_state.VolumeFactor, 1.0, 1.60);
            _smoothedVolumeFactor += (targetVolume - _smoothedVolumeFactor) * (1.0 - Math.Exp(-dt / 0.150));

            double targetPlayBlend = canRun ? 1.0 : 0.0;
            double prevPlayBlend = _playingBlend;
            _playingBlend += (targetPlayBlend - _playingBlend) * (1.0 - Math.Exp(-dt / 0.240));
            if (Math.Abs(targetPlayBlend - _playingBlend) < 0.005)
                _playingBlend = targetPlayBlend;

            bool shaderAnimated = Math.Abs(_playingBlend - prevPlayBlend) > 1e-4;
            bool isFluidMode = _state.Mode == AmbientBackdropMode.FluidWave;
            bool hasActiveAudio = false;

            if (canRun && AudioVisualizerBuffer.TryReadSnapshot(out var snap))
            {
                hasActiveAudio = true;
                double excess = Math.Clamp(_smoothedVolumeFactor - 1.0, 0.0, 0.60);
                double ampBoost = 1.0 + (0.35 * excess);

                double macroTarget = snap.MacroEnergy;
                double macroAlpha = macroTarget > _macroEnergy
                    ? (1.0 - Math.Exp(-dt / 0.085))
                    : (1.0 - Math.Exp(-dt / 0.360));
                _macroEnergy += (macroTarget - _macroEnergy) * macroAlpha;

                _vocalAura += (snap.Vocal - _vocalAura) * (1.0 - Math.Exp(-dt / (snap.Vocal > _vocalAura ? 0.075 : 0.220)));
                _harmonicEnergy += (snap.HarmonicEnergy - _harmonicEnergy) * (1.0 - Math.Exp(-dt / 0.140));
                _dropBloom = Math.Max(snap.DropImpact, _dropBloom * Math.Exp(-dt / 0.450));
                double rawShimmer = Math.Clamp((snap.HighFlux * 0.85) + (snap.SnareFlux * 0.55) + (snap.MidFlux * 0.30) + (snap.High * 0.25), 0.0, 1.0);
                _shimmerEnergy += (rawShimmer - _shimmerEnergy) * (1.0 - Math.Exp(-dt / (rawShimmer > _shimmerEnergy ? 0.035 : 0.160)));
                _centroid += (snap.SpectralCentroid - _centroid) * (1.0 - Math.Exp(-dt / 0.140));

                _signal.AdvanceCooldowns(dt);

                double kickImpulse = Math.Max(snap.Beat, snap.BassFlux * 0.90) * ampBoost;
                if (kickImpulse > 0.20 && _signal.CooldownBeat <= 0.0)
                {
                    _kickSpringVel += (kickImpulse * 6.8) + (snap.DropImpact * 4.0);
                    if (isFluidMode)
                    {
                        SpawnFluidPacketCore(
                            _waves,
                            halfWidth: 0.21f,
                            speed: (float)(0.50 + (0.14 * kickImpulse)),
                            amplitude: (float)Math.Clamp((kickImpulse * 0.88) + (snap.DropImpact * 0.32), 0.28, 1.05),
                            tint: -0.72f);
                    }
                    else
                    {
                        SpawnAuroraRipple(
                            center: _isBottom ? (float)_rng.NextDouble(0.32, 0.68) : (float)_rng.NextDouble(0.38, 0.62),
                            speed: (float)(0.85 + (0.35 * kickImpulse)),
                            amplitude: (float)Math.Clamp(kickImpulse * 0.95 * ampBoost, 0.25, 1.0),
                            decayRate: 1.45f,
                            tint: -0.65f);
                    }
                    _signal.CooldownBeat = 0.24;
                }

                if (isFluidMode && snap.BassFlux > 0.16 && snap.Low > 0.18 && _signal.CooldownLow <= 0.0 && _signal.CooldownBeat < 0.10)
                {
                    SpawnFluidPacketCore(
                        _waves,
                        halfWidth: 0.26f,
                        speed: 0.41f,
                        amplitude: (float)Math.Clamp(((snap.BassFlux * 0.65) + (snap.Low * 0.22)) * ampBoost, 0.22, 0.82),
                        tint: -0.52f);
                    _signal.CooldownLow = 0.34;
                }

                // MidFlux captures quiet sci-fi / synth / melodic impulses in both FluidWave & Aurora
                double midDrive = Math.Max(snap.MidFlux, snap.SnareFlux * 0.90);
                if (midDrive > 0.14 && _signal.CooldownMid <= 0.0)
                {
                    _kickSpringVel += (snap.SnareFlux * 3.2) + (snap.MidFlux * 1.8);
                    float tint = snap.SnareFlux > snap.MidFlux ? 0.72f : (float)Math.Clamp((_centroid - 0.42) * 1.3, -0.45, 0.65);

                    if (isFluidMode)
                    {
                        SpawnFluidPacketCore(
                            _waves,
                            halfWidth: snap.SnareFlux > snap.MidFlux ? 0.115f : 0.155f,
                            speed: snap.SnareFlux > snap.MidFlux ? 0.78f : 0.64f,
                            amplitude: (float)Math.Clamp(midDrive * 0.82 * ampBoost, 0.24, 0.88),
                            tint: tint);
                    }
                    else
                    {
                        SpawnAuroraRipple(
                            center: (float)_rng.NextDouble(0.22, 0.78),
                            speed: snap.SnareFlux > snap.MidFlux ? 1.15f : 0.95f,
                            amplitude: (float)Math.Clamp(midDrive * 0.82 * ampBoost, 0.20, 0.90),
                            decayRate: 1.75f,
                            tint: tint);
                    }
                    _signal.CooldownMid = 0.24;
                }

                if (snap.HighFlux > 0.18 && _signal.CooldownHigh <= 0.0)
                {
                    if (isFluidMode)
                    {
                        SpawnFluidPacketCore(
                            _waves,
                            halfWidth: 0.095f,
                            speed: 0.90f,
                            amplitude: (float)Math.Clamp(snap.HighFlux * 0.66 * ampBoost, 0.18, 0.72),
                            tint: 0.92f);
                    }
                    else
                    {
                        SpawnAuroraRipple(
                            center: (float)_rng.NextDouble(0.15, 0.85),
                            speed: 1.35f,
                            amplitude: (float)Math.Clamp(snap.HighFlux * 0.65 * ampBoost, 0.16, 0.70),
                            decayRate: 2.30f,
                            tint: 0.95f);
                    }
                    _signal.CooldownHigh = 0.20;
                }

                _signal.SmoothToward(snap.Beat, snap.Low, snap.Mid, snap.High, dt, 0.065, 0.110, 0.100, 0.080);

                double carrierRate = 0.45 + (0.65 * _macroEnergy) + (0.55 * Math.Max(0.0, _kickSpringPos)) + (0.35 * _vocalAura);
                _waveCarrierPhase = (_waveCarrierPhase + (dt * carrierRate)) % CarrierWrapPeriod;

                double lowTarget = Math.Clamp((_signal.Low * 0.65) + ((1.0 - _centroid) * 0.40), 0.05, 1.2);
                double midTarget = Math.Clamp((_signal.Mid * 0.45) + (_vocalAura * 0.85), 0.05, 1.4);
                double highTarget = Math.Clamp((_signal.High * 0.60) + (_centroid * 0.50) + (_shimmerEnergy * 0.35), 0.05, 1.3);
                double total = lowTarget + midTarget + highTarget;
                _lowWeight += ((lowTarget / total) - _lowWeight) * weightAlpha;
                _midWeight += ((midTarget / total) - _midWeight) * weightAlpha;
                _highWeight += ((highTarget / total) - _highWeight) * weightAlpha;

                shaderAnimated = true;
            }
            else
            {
                bool hadDecayingEnvelopes = _macroEnergy > 0.015
                                         || _vocalAura > 0.01
                                         || _harmonicEnergy > 0.01
                                         || _dropBloom > 0.01
                                         || _shimmerEnergy > 0.01;
                double decay = Math.Exp(-dt / 0.220);
                _signal.Decay(dt, 0.200);
                _macroEnergy *= decay;
                _vocalAura *= decay;
                _harmonicEnergy *= decay;
                _dropBloom *= decay;
                _shimmerEnergy *= decay;
                _lowWeight += (NeutralBandWeight - _lowWeight) * weightAlpha;
                _midWeight += (NeutralBandWeight - _midWeight) * weightAlpha;
                _highWeight += (NeutralBandWeight - _highWeight) * weightAlpha;

                if (hadDecayingEnvelopes)
                    shaderAnimated = true;
            }

            // Integrate 2nd-order spring (stiffness=160, damping=16 -> crisp punch with natural elastic settle)
            double springForce = (-160.0 * _kickSpringPos) - (16.0 * _kickSpringVel);
            _kickSpringVel += springForce * dt;
            _kickSpringPos = Math.Clamp(_kickSpringPos + (_kickSpringVel * dt), -0.15, 1.20);
            if (Math.Abs(_kickSpringPos) > 0.005 || Math.Abs(_kickSpringVel) > 0.02)
                shaderAnimated = true;

            float fdt = (float)dt;
            if (isFluidMode)
            {
                if (StepFluidPacketsCore(_waves, fdt, isPlaying: canRun && hasActiveAudio))
                    shaderAnimated = true;
            }
            else
            {
                for (int i = 0; i < MaxAuroraRipples; i++)
                {
                    ref var r = ref _waves[i];
                    if (!r.IsActive) continue;

                    r.Age += fdt;
                    r.Param += r.Speed * fdt;
                    r.Amplitude *= MathF.Exp(-r.DecayRate * fdt);
                    if (r.Amplitude <= 0.005f || r.Param > 1.20f)
                        r.IsActive = false;
                    else
                        shaderAnimated = true;
                }
            }

            return shaderAnimated;
        }

        private void SpawnAuroraRipple(float center, float speed, float amplitude, float decayRate, float tint)
        {
            int slot = -1;
            float weakest = 0.16f;
            for (int i = 0; i < MaxAuroraRipples; i++)
            {
                if (!_waves[i].IsActive)
                {
                    slot = i;
                    break;
                }
                if (_waves[i].Amplitude < weakest)
                {
                    weakest = _waves[i].Amplitude;
                    slot = i;
                }
            }

            if (slot < 0) return;

            ref var r = ref _waves[slot];
            r.IsActive = true;
            r.Position = center;
            r.Param = 0f;
            r.Speed = speed;
            r.Amplitude = amplitude;
            r.Age = 0f;
            r.DecayRate = decayRate * 1.4f;
            r.PauseFade = 1f;
            r.Tint = tint;
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
            double volumeGain = 0.94 + (0.36 * excess);
            double purityScale = _state.IsMixed ? 0.76 : 1.0;

            double kickPos = Math.Max(0.0, _kickSpringPos);
            double dynamicEnergy = ComputeDynamicEnergy(_playingBlend, _macroEnergy, kickPos, _vocalAura, _dropBloom, _state.Reactivity);

            double energyScale = purityScale * _state.GlobalIntensity * adaptiveLumScale * volumeGain * dynamicEnergy;
            bool hasGpuShader = ResolveEffectForMode(_state.Mode) != null;
            double masterScale = hasGpuShader ? ShaderMasterScale : FallbackMasterScale;
            double presence = _isBottom ? (_state.ScrollablePresence * BottomIntensityScale) : _state.TopPresence;

            _masterAlpha = (byte)Math.Clamp((int)Math.Round(masterScale * Math.Clamp(presence * energyScale, 0.0, 1.0)), 0, 255);

            double reactScale = 0.35 + (0.65 * _state.Reactivity);
            double musicBreath = ((0.18 * Math.Clamp(_macroEnergy, 0.0, 1.35))
                                + (0.14 * kickPos)
                                + (0.10 * _dropBloom)) * reactScale;
            _scaleY = _isBottom
                ? (float)Math.Clamp(0.88 + (0.26 * excess) + (0.80 * musicBreath), 0.80, ComputeMaxScaleY())
                : (float)Math.Clamp(0.88 + (0.30 * excess) + musicBreath, 0.80, ComputeMaxScaleY());
        }

        private void DisposeRuntimeShader()
        {
            _cachedFrameImage?.Dispose();
            _cachedFrameImage = null;
            _shaderSurface?.Dispose();
            _shaderSurface = null;
            _surfaceGrContext = null;
            _cachedRuntimeShader?.Dispose();
            _cachedRuntimeShader = null;
            _shaderUniforms?.Dispose();
            _shaderUniforms = null;
            _boundEffect = null;
            _uniformsPacked = false;
            _shaderDirty = true;
        }

        private void DisposeCachedResources()
        {
            _cachedFallbackShader?.Dispose();
            _cachedFallbackShader = null;
            _cachedFallbackRgb = 0;

            DisposeRuntimeShader();
        }

        private void DrawSmoothWashFallback(SKCanvas canvas, SKPaint paint, float width, byte effectiveAlpha)
        {
            uint rgbKey = ToQuantizedRgbKey(_blendedRgb);
            if (_cachedFallbackShader == null || _cachedFallbackRgb != rgbKey)
            {
                _cachedFallbackShader?.Dispose();
                var colors = t_gradColors ??= new SKColor[5];
                byte r = _blendedRgb.Red;
                byte g = _blendedRgb.Green;
                byte b = _blendedRgb.Blue;
                colors[0] = new SKColor(r, g, b, 132);
                colors[1] = new SKColor(r, g, b, 200);
                colors[2] = new SKColor(r, g, b, 82);
                colors[3] = new SKColor(r, g, b, 22);
                colors[4] = new SKColor(r, g, b, 0);

                _cachedFallbackShader = SKShader.CreateLinearGradient(
                    new SKPoint(0f, 0f),
                    new SKPoint(0f, WashHeight),
                    colors,
                    WaveStops,
                    SKShaderTileMode.Clamp);
                _cachedFallbackRgb = rgbKey;
            }

            paint.IsAntialias = false;
            paint.IsDither = true;
            paint.Shader = _cachedFallbackShader;
            paint.Color = new SKColor(255, 255, 255, effectiveAlpha);
            canvas.DrawRect(0f, 0f, width, WashHeight, paint);
        }

        private void DrawRuntimeShader(
            SKCanvas canvas,
            GRContext? grContext,
            SKPaint paint,
            SKRuntimeEffect effect,
            float width,
            byte effectiveAlpha)
        {
            if (_cachedFrameImage == null
                || _shaderSurface == null
                || !ReferenceEquals(_boundEffect, effect)
                || !ReferenceEquals(_surfaceGrContext, grContext)
                || _shaderDirty)
            {
                RenderOffscreenShaderFrame(grContext, effect);
            }

            if (_cachedFrameImage == null)
                return;

            paint.IsAntialias = false;
            paint.IsDither = true;
            paint.Shader = null;
            paint.Color = new SKColor(255, 255, 255, effectiveAlpha);
            canvas.DrawImage(_cachedFrameImage, new SKRect(0f, 0f, width, WashHeight), LinearSampling, paint);
        }

        private void PackUniformArrays()
        {
            const float inv255 = 1f / 255f;

            _uColors[0] = _state.ColorLow.Red * inv255;
            _uColors[1] = _state.ColorLow.Green * inv255;
            _uColors[2] = _state.ColorLow.Blue * inv255;
            _uColors[3] = 1f;
            _uColors[4] = _state.ColorMid.Red * inv255;
            _uColors[5] = _state.ColorMid.Green * inv255;
            _uColors[6] = _state.ColorMid.Blue * inv255;
            _uColors[7] = 1f;
            _uColors[8] = _state.ColorHigh.Red * inv255;
            _uColors[9] = _state.ColorHigh.Green * inv255;
            _uColors[10] = _state.ColorHigh.Blue * inv255;
            _uColors[11] = 1f;

            _uParams[0] = ShaderTexWidth;
            _uParams[1] = ShaderTexHeight;
            _uParams[2] = 0f; // Page mode
            _uParams[3] = (float)Math.Clamp(_state.Reactivity, 0.0, 1.0);
            _uParams[4] = (float)Math.Clamp(_macroEnergy, 0.0, 1.5);
            _uParams[5] = (float)Math.Clamp(_kickSpringPos, 0.0, 1.2);
            _uParams[6] = (float)Math.Clamp(_vocalAura, 0.0, 1.0);
            _uParams[7] = (float)Math.Clamp(_dropBloom, 0.0, 1.0);
            _uParams[8] = (float)(_waveCarrierPhase + (_isBottom ? 2.1 : 0.0));
            _uParams[9] = (float)Math.Clamp(_centroid, 0.0, 1.0);
            _uParams[10] = (float)Math.Clamp(_shimmerEnergy, 0.0, 1.0);
            _uParams[11] = (float)Math.Clamp(_harmonicEnergy, 0.0, 1.0);

            if (_state.Mode == AmbientBackdropMode.FluidWave)
            {
                for (int i = 0; i < MaxFluidWavePackets; i++)
                {
                    int off = i * 4;
                    ref readonly var w = ref _waves[i];
                    if (w.IsActive)
                    {
                        _uWavesFluid[off] = w.Position;
                        _uWavesFluid[off + 1] = 1f / MathF.Max(0.05f, w.Param);
                        _uWavesFluid[off + 2] = ComputeFluidEffectiveAmplitude(in w);
                        _uWavesFluid[off + 3] = w.Tint;
                    }
                    else
                    {
                        _uWavesFluid[off] = -1f;
                        _uWavesFluid[off + 1] = 5f;
                        _uWavesFluid[off + 2] = 0f;
                        _uWavesFluid[off + 3] = 0f;
                    }
                }
            }
            else
            {
                for (int i = 0; i < MaxAuroraRipples; i++)
                {
                    int off = i * 4;
                    ref readonly var r = ref _waves[i];
                    if (r.IsActive)
                    {
                        float radiusFade = 1f - SmoothStepF((r.Param - 0.90f) * (1f / 0.30f));
                        _uWavesAurora[off] = r.Position;
                        _uWavesAurora[off + 1] = r.Param;
                        _uWavesAurora[off + 2] = r.Amplitude * radiusFade;
                        _uWavesAurora[off + 3] = r.Tint;
                    }
                    else
                    {
                        _uWavesAurora[off] = 0.5f;
                        _uWavesAurora[off + 1] = 0f;
                        _uWavesAurora[off + 2] = 0f;
                        _uWavesAurora[off + 3] = 0f;
                    }
                }
            }
        }

        private void RenderOffscreenShaderFrame(GRContext? grContext, SKRuntimeEffect effect)
        {
            if (_shaderSurface == null || !ReferenceEquals(_surfaceGrContext, grContext))
            {
                _cachedFrameImage?.Dispose();
                _cachedFrameImage = null;
                _shaderSurface?.Dispose();
                var info = new SKImageInfo(ShaderTexWidth, ShaderTexHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                _shaderSurface = grContext != null
                    ? SKSurface.Create(grContext, budgeted: true, info) ?? SKSurface.Create(info)
                    : SKSurface.Create(info);
                _surfaceGrContext = grContext;
            }

            if (_shaderSurface == null)
                return;

            if (!_uniformsPacked)
                PackUniformArrays();
            _uniformsPacked = false;

            if (!ReferenceEquals(_boundEffect, effect))
            {
                _shaderUniforms?.Dispose();
                _shaderUniforms = new SKRuntimeEffectUniforms(effect);
                _boundEffect = effect;
            }

            var uniforms = _shaderUniforms!;
            uniforms["uColors"] = _uColors;
            uniforms["uParams"] = _uParams;
            uniforms["uWaves"] = _state.Mode == AmbientBackdropMode.FluidWave ? _uWavesFluid : _uWavesAurora;

            _cachedRuntimeShader?.Dispose();
            _cachedRuntimeShader = effect.ToShader(uniforms);
            if (_cachedRuntimeShader == null)
                return;

            // Dispose previous snapshot BEFORE drawing so Skia reuses the GPU texture in-place without copy-on-write
            _cachedFrameImage?.Dispose();
            _cachedFrameImage = null;

            var texPaint = t_texPaint ??= new SKPaint
            {
                IsAntialias = false,
                BlendMode = SKBlendMode.Src
            };
            texPaint.Shader = _cachedRuntimeShader;
            _shaderSurface.Canvas.DrawRect(0f, 0f, ShaderTexWidth, ShaderTexHeight, texPaint);
            texPaint.Shader = null;
            _cachedFrameImage = _shaderSurface.Snapshot();
            _shaderDirty = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ToQuantizedRgbKey(SKColor c) =>
            (((uint)c.Red & 0xFC) << 16) | (((uint)c.Green & 0xFC) << 8) | ((uint)c.Blue & 0xFC);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ToRgbKey(SKColor c) =>
            ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
    }

    private static readonly Lock s_sharedStateLock = new();
    private static readonly WaveSlot[] s_sharedWaves = new WaveSlot[MaxFluidWavePackets];
    private static AmbientBackdropMode s_sharedMode;
    private static AudioReactiveSignalState s_sharedSignal;
    private static double s_sharedMacroEnergy;
    private static double s_sharedVocalAura;
    private static double s_sharedCarrierPhase;
    private static long s_sharedTimestamp;
    private static bool s_hasSharedState;

    private static void PublishSharedBackdropState(
        AmbientBackdropMode mode,
        WaveSlot[] waves,
        in AudioReactiveSignalState signal,
        double macroEnergy,
        double vocalAura,
        double carrierPhase)
    {
        lock (s_sharedStateLock)
        {
            s_sharedMode = mode;
            Array.Copy(waves, s_sharedWaves, MaxFluidWavePackets);
            s_sharedSignal = signal;
            s_sharedMacroEnergy = macroEnergy;
            s_sharedVocalAura = vocalAura;
            s_sharedCarrierPhase = carrierPhase;
            s_sharedTimestamp = Stopwatch.GetTimestamp();
            s_hasSharedState = true;
        }
    }

    private static void TryRestoreSharedBackdropState(
        AmbientBackdropMode mode,
        bool isBottom,
        WaveSlot[] targetWaves,
        ref AudioReactiveSignalState signal,
        ref double macroEnergy,
        ref double vocalAura,
        ref double carrierPhase)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec;

        lock (s_sharedStateLock)
        {
            if (!s_hasSharedState || s_sharedMode != mode)
                return;

            elapsedSec = Stopwatch.GetElapsedTime(s_sharedTimestamp, now).TotalSeconds;
            if (elapsedSec > 4.0)
                return;

            Array.Copy(s_sharedWaves, targetWaves, MaxFluidWavePackets);
            signal = s_sharedSignal;
            macroEnergy = s_sharedMacroEnergy;
            vocalAura = s_sharedVocalAura;
            carrierPhase = s_sharedCarrierPhase;
        }

        double dt = Math.Clamp(elapsedSec, 0.0, 0.6);
        carrierPhase = (carrierPhase + (dt * 0.80) + (isBottom ? 2.1 : 0.0)) % CarrierWrapPeriod;

        float fdt = (float)dt;
        if (mode == AmbientBackdropMode.FluidWave)
        {
            StepFluidPacketsCore(targetWaves, fdt, isPlaying: true);
        }
        else
        {
            for (int i = 0; i < MaxAuroraRipples; i++)
            {
                ref var r = ref targetWaves[i];
                if (!r.IsActive) continue;

                r.Age += fdt;
                r.Param += r.Speed * fdt;
                r.Amplitude *= MathF.Exp(-r.DecayRate * fdt);
                if (r.Amplitude <= 0.005f || r.Param > 1.20f)
                    r.IsActive = false;
            }
        }
    }

    private static AmbientBackdropMode _globalMode = AmbientBackdropMode.FluidWave;
    private static double _globalIntensity = 1.0;
    private static double _globalReactivity = 0.60;
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

    internal static double CurrentReactivity
    {
        get
        {
            EnsureGlobalConfigLoaded();
            return _globalReactivity;
        }
    }

    public static void ConfigureGlobal(AmbientBackdropMode mode, int intensityPercent, int reactivityPercent = 60)
    {
        _globalMode = mode;
        _globalIntensity = Math.Clamp(intensityPercent, 10, 200) / 100.0;
        _globalReactivity = Math.Clamp(reactivityPercent, 0, 100) / 100.0;
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
                _globalReactivity = Math.Clamp(settings.AmbientBackdropReactivity, 0, 100) / 100.0;
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
        // Scale departure distance proportionally to actual list length so long lists keep rich top glow
        // and transition smoothly to the bottom glow as scrollProgress approaches 100%.
        double departureSpan = Math.Clamp(_scrollMaxOffsetY * 0.25, 450.0, 2400.0);
        double subtleDeparture = 1.0 - (0.12 * SmoothStep(_scrollOffsetY / departureSpan));
        double progressFade = 1.0 - (0.58 * SmoothStep((scrollProgress - 0.10) / 0.90));
        return Math.Max(0.32, subtleDeparture * progressFade);
    }

    private bool ComputeBottomVisible(double height, double volumeFactor)
    {
        if (!BaseColor.HasValue || _globalMode == AmbientBackdropMode.Off)
            return false;

        if (!_showBottomWash || _scrollMaxOffsetY <= 40.0 || height <= _viewportTopOffset + 40.0)
            return false;

        double excess = Math.Clamp(volumeFactor - 1.0, 0.0, 0.60);
        double maxBottomScaleY = 1.24 + (0.35 * excess);
        double bottomAnchorY = _viewportTopOffset + _scrollExtentHeight - _scrollOffsetY;
        double bottomSpatialY = bottomAnchorY - (WashHeight * maxBottomScaleY);
        return bottomSpatialY < height && bottomAnchorY > _viewportTopOffset;
    }

    private void UpdateEffectiveTopFactor()
    {
        double factor = ComputeTopPresence() * (IsMixed ? 0.72 : 1.0);
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
                Reactivity = _globalReactivity,
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

        BuildPaletteColors(baseColor.Value, out _colorLow, out _colorMid, out _colorHigh);
    }

    internal static void BuildPaletteColors(Color baseColor, out SKColor low, out SKColor mid, out SKColor high)
    {
        var baseLifted = LiftAmbientColor(baseColor);
        var cLow = ShiftColor(baseLifted, hueDeltaDegrees: -14f, lightScale: 0.86f, lightClampMin: 0.24f, lightClampMax: 0.48f);
        var cHigh = ShiftColor(baseLifted, hueDeltaDegrees: 16f, lightScale: 1.14f, lightClampMin: 0.36f, lightClampMax: 0.64f);

        low = new SKColor(cLow.R, cLow.G, cLow.B);
        mid = new SKColor(baseLifted.R, baseLifted.G, baseLifted.B);
        high = new SKColor(cHigh.R, cHigh.G, cHigh.B);
    }

    /// <summary>
    /// Computes smooth, discontinuity-free backdrop luminance energy with a raised visibility floor (+30% min),
    /// comfortable peak ceiling (-20% max), and user-configurable dynamic reactivity (<paramref name="reactivity"/> in [0..1]).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double ComputeDynamicEnergy(
        double playingBlend,
        double macroEnergy,
        double kickPos,
        double vocalAura,
        double dropBloom,
        double reactivity = 0.60)
    {
        double r = Math.Clamp(reactivity, 0.0, 1.0);
        const double restingEnergy = 0.62;
        double rawDrive = (0.48 * Math.Clamp(macroEnergy, 0.0, 1.35))
                        + (0.24 * Math.Max(0.0, kickPos))
                        + (0.14 * Math.Clamp(vocalAura, 0.0, 1.0))
                        + (0.22 * Math.Clamp(dropBloom, 0.0, 1.0));
        double drive = 1.0 - Math.Exp(-rawDrive * 1.15);
        double floorEnergy = 0.64 - (0.20 * r);
        double activeEnergy = floorEnergy + (drive * 0.72 * r);
        double blend = Math.Clamp(playingBlend, 0.0, 1.0);
        return restingEnergy + ((activeEnergy - restingEnergy) * blend);
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
        s = Math.Clamp(s, 0.24f, 0.86f);
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

/// <summary>
/// Live render-thread TitleBar continuation of the active page's <see cref="AmbientBackdrop"/>.
/// Renders the exact same horizontal wave/aurora GPU SkSL profile right above the top seam (<c>y = 46px</c>),
/// fading organically toward the top window edge with 0 UI-thread per-frame wakeups and 0 B allocations.
/// </summary>
public sealed class AmbientTitleBarBleed : Control
{
    private static readonly object MsgStop = new();
    private static readonly object MsgSyncState = new();

    private struct TitleBarStateSnapshot
    {
        public bool IsEnabled;
        public AmbientBackdropMode Mode;
        public double GlobalIntensity;
        public double Reactivity;
        public double BleedOpacity;
        public SKColor ColorLow;
        public SKColor ColorMid;
        public SKColor ColorHigh;
        public uint ColorMidKey;
    }

    private sealed class TitleBarBleedHandler : CompositionCustomVisualHandler, AmbientBackdrop.ILiveSeamListener
    {
        [ThreadStatic] private static SKPaint? t_paint;
        [ThreadStatic] private static SKPaint? t_texPaint;
        [ThreadStatic] private static SKColor[]? t_gradColors;
        private static readonly float[] FallbackStops = [0f, 1f];

        private readonly AmbientTitleBarBleed _owner;
        private readonly float[] _uColors = new float[12];
        private readonly float[] _uParams = new float[12];
        private readonly float[] _uWavesFluid = new float[AmbientBackdrop.MaxFluidWavePackets * 4];
        private readonly float[] _uWavesAurora = new float[4 * 4];

        private TitleBarStateSnapshot _state;
        private AmbientBackdropMode _activeMode;
        private byte _masterAlpha;
        private SKColor _blendedRgb;
        private long _lastSeamSeq = -1;
        private bool _usingLiveSeam;
        private bool _hasValidContentBounds;
        private bool _isListenerRegistered;

        private SKRuntimeEffect? _boundEffect;
        private SKRuntimeEffectUniforms? _shaderUniforms;
        private SKShader? _cachedShader;
        private SKSurface? _shaderSurface;
        private GRContext? _surfaceGrContext;
        private SKImage? _cachedFrameImage;
        private SKShader? _cachedFallbackShader;
        private uint _cachedFallbackKey;
        private float _cachedFallbackHeight = -1f;
        private bool _dirty = true;

        public TitleBarBleedHandler(AmbientTitleBarBleed owner)
        {
            _owner = owner;
        }

        public override Rect GetRenderBounds()
        {
            if (!_state.IsEnabled || _masterAlpha == 0 || EffectiveSize.X <= 0.0 || EffectiveSize.Y <= 0.0)
                return default;

            return new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y);
        }

        private void InvalidateDirtyUnion(Rect prevBounds, Rect nextBounds)
        {
            bool hasPrev = prevBounds.Width > 0.0 && prevBounds.Height > 0.0;
            bool hasNext = nextBounds.Width > 0.0 && nextBounds.Height > 0.0;

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

        /// <summary>
        /// Invoked directly on the compositor render thread whenever the active page's <see cref="AmbientBackdrop"/>
        /// updates its top seam (audio frames, scroll, pause, palette/mode change). Zero cross-thread marshaling.
        /// </summary>
        public void OnLiveSeamUpdated()
        {
            if (!_isListenerRegistered || !_state.IsEnabled)
                return;

            bool wasLiveSeam = _usingLiveSeam;
            var prevBounds = GetRenderBounds();
            long prevSeq = _lastSeamSeq;
            byte prevAlpha = _masterAlpha;

            RefreshFromSeamOrFallback();
            var nextBounds = GetRenderBounds();

            if (wasLiveSeam != _usingLiveSeam || _lastSeamSeq != prevSeq || _masterAlpha != prevAlpha || prevBounds != nextBounds)
            {
                _dirty = true;
                InvalidateDirtyUnion(prevBounds, nextBounds);
            }
        }

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, MsgStop))
            {
                var prevBounds = GetRenderBounds();
                if (_isListenerRegistered)
                {
                    AmbientBackdrop.UnregisterTitleBarListener(this);
                    _isListenerRegistered = false;
                }

                _masterAlpha = 0;
                _usingLiveSeam = false;
                _state.IsEnabled = false;
                InvalidateDirtyUnion(prevBounds, default);
                DisposeShaders();
                return;
            }

            if (ReferenceEquals(message, MsgSyncState))
            {
                if (!_isListenerRegistered)
                {
                    AmbientBackdrop.RegisterTitleBarListener(this);
                    _isListenerRegistered = true;
                }

                var prevBounds = GetRenderBounds();
                SyncFromOwner();

                if (!_state.IsEnabled)
                {
                    _masterAlpha = 0;
                    _lastSeamSeq = -1;
                    _usingLiveSeam = false;
                    if (_state.Mode == AmbientBackdropMode.Off)
                        DisposeShaders();
                    InvalidateDirtyUnion(prevBounds, default);
                    return;
                }

                RefreshFromSeamOrFallback();
                _dirty = true;
                InvalidateDirtyUnion(prevBounds, GetRenderBounds());
            }
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (!_state.IsEnabled || _masterAlpha == 0)
                return;

            float w = (float)EffectiveSize.X;
            float h = (float)EffectiveSize.Y;
            if (w <= 1f || h <= 1f)
                return;

            var leaseFeature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            byte effectiveAlpha = (byte)Math.Clamp((int)Math.Round(_masterAlpha * lease.CurrentOpacity), 0, 255);
            if (effectiveAlpha == 0) return;

            var canvas = lease.SkCanvas;
            var paint = t_paint ??= new SKPaint();
            paint.Reset();
            paint.IsAntialias = false;
            paint.IsDither = true;

            var effect = AmbientBackdrop.ResolveEffectForMode(_activeMode);
            if (effect != null)
            {
                if (_cachedFrameImage == null
                    || _shaderSurface == null
                    || !ReferenceEquals(_boundEffect, effect)
                    || !ReferenceEquals(_surfaceGrContext, lease.GrContext)
                    || _dirty)
                {
                    RenderOffscreenTitleBarFrame(lease.GrContext, effect);
                }

                if (_cachedFrameImage != null)
                {
                    paint.Shader = null;
                    paint.Color = new SKColor(255, 255, 255, effectiveAlpha);
                    canvas.DrawImage(_cachedFrameImage, new SKRect(0f, 0f, w, h), AmbientBackdrop.LinearSampling, paint);
                }
            }
            else
            {
                DrawFallbackGradient(canvas, paint, w, h, effectiveAlpha);
            }

            paint.Shader = null;
            paint.Reset();
        }

        private void SyncFromOwner()
        {
            lock (_owner._stateLock)
            {
                _state = _owner._snapshot;
                _owner._syncQueued = false;
            }
        }

        private void RefreshFromSeamOrFallback()
        {
            if (AmbientBackdrop.TryReadLiveSeamState(
                    _state.ColorMidKey,
                    ref _lastSeamSeq,
                    out var seamMode,
                    out byte seamAlpha,
                    out var seamRgb,
                    _uColors,
                    _uParams,
                    _uWavesFluid,
                    _uWavesAurora))
            {
                _usingLiveSeam = true;
                _activeMode = seamMode;
                _masterAlpha = seamAlpha;
                _blendedRgb = seamRgb;
                return;
            }

            // Fallback resting strip before the page backdrop publishes its first frame or after live seam clears
            _usingLiveSeam = false;
            _lastSeamSeq = -1;
            _activeMode = _state.Mode;
            _blendedRgb = _state.ColorMid;

            double adaptiveLum = AmbientBackdrop.ComputeAdaptiveBrightnessScale(
                Color.FromRgb(_state.ColorMid.Red, _state.ColorMid.Green, _state.ColorMid.Blue));
            double resting = AmbientBackdrop.ComputeDynamicEnergy(0.0, 0.0, 0.0, 0.0, 0.0, _state.Reactivity);
            double scale = _state.BleedOpacity * _state.GlobalIntensity * adaptiveLum * resting * 0.94;
            _masterAlpha = (byte)Math.Clamp((int)Math.Round(196.0 * Math.Clamp(scale, 0.0, 1.0)), 0, 255);

            const float inv255 = 1f / 255f;
            _uColors[0] = _state.ColorLow.Red * inv255;
            _uColors[1] = _state.ColorLow.Green * inv255;
            _uColors[2] = _state.ColorLow.Blue * inv255;
            _uColors[3] = 1f;
            _uColors[4] = _state.ColorMid.Red * inv255;
            _uColors[5] = _state.ColorMid.Green * inv255;
            _uColors[6] = _state.ColorMid.Blue * inv255;
            _uColors[7] = 1f;
            _uColors[8] = _state.ColorHigh.Red * inv255;
            _uColors[9] = _state.ColorHigh.Green * inv255;
            _uColors[10] = _state.ColorHigh.Blue * inv255;
            _uColors[11] = 1f;

            Array.Clear(_uParams);
            _uParams[3] = (float)Math.Clamp(_state.Reactivity, 0.0, 1.0);
            _uParams[9] = 0.40f;
            Array.Clear(_uWavesFluid);
            Array.Clear(_uWavesAurora);
        }

        private void RenderOffscreenTitleBarFrame(GRContext? grContext, SKRuntimeEffect effect)
        {
            if (_shaderSurface == null || !ReferenceEquals(_surfaceGrContext, grContext))
            {
                _cachedFrameImage?.Dispose();
                _cachedFrameImage = null;
                _shaderSurface?.Dispose();
                var info = new SKImageInfo(AmbientBackdrop.ShaderTexWidth, AmbientBackdrop.TitleBarTexHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                _shaderSurface = grContext != null
                    ? SKSurface.Create(grContext, budgeted: true, info) ?? SKSurface.Create(info)
                    : SKSurface.Create(info);
                _surfaceGrContext = grContext;
            }

            if (_shaderSurface == null)
                return;

            _uParams[0] = AmbientBackdrop.ShaderTexWidth;
            _uParams[1] = AmbientBackdrop.TitleBarTexHeight;
            _uParams[2] = 1f; // TitleBar upward continuation mode (seamless 1:1 match with page v=0)

            if (!ReferenceEquals(_boundEffect, effect))
            {
                _shaderUniforms?.Dispose();
                _shaderUniforms = new SKRuntimeEffectUniforms(effect);
                _boundEffect = effect;
            }

            var uniforms = _shaderUniforms!;
            uniforms["uColors"] = _uColors;
            uniforms["uParams"] = _uParams;
            uniforms["uWaves"] = _activeMode == AmbientBackdropMode.FluidWave ? _uWavesFluid : _uWavesAurora;

            _cachedShader?.Dispose();
            _cachedShader = effect.ToShader(uniforms);
            if (_cachedShader == null)
                return;

            _cachedFrameImage?.Dispose();
            _cachedFrameImage = null;

            var texPaint = t_texPaint ??= new SKPaint
            {
                IsAntialias = false,
                BlendMode = SKBlendMode.Src
            };
            texPaint.Shader = _cachedShader;
            _shaderSurface.Canvas.DrawRect(0f, 0f, AmbientBackdrop.ShaderTexWidth, AmbientBackdrop.TitleBarTexHeight, texPaint);
            texPaint.Shader = null;
            _cachedFrameImage = _shaderSurface.Snapshot();
            _dirty = false;
        }

        private void DrawFallbackGradient(SKCanvas canvas, SKPaint paint, float w, float h, byte effectiveAlpha)
        {
            uint key = (((uint)_blendedRgb.Red & 0xFC) << 16) | (((uint)_blendedRgb.Green & 0xFC) << 8) | ((uint)_blendedRgb.Blue & 0xFC);
            if (_cachedFallbackShader == null || _cachedFallbackKey != key || MathF.Abs(_cachedFallbackHeight - h) > 0.5f)
            {
                _cachedFallbackShader?.Dispose();
                var colors = t_gradColors ??= new SKColor[2];
                colors[0] = new SKColor(_blendedRgb.Red, _blendedRgb.Green, _blendedRgb.Blue, 36);
                colors[1] = new SKColor(_blendedRgb.Red, _blendedRgb.Green, _blendedRgb.Blue, 156);
                _cachedFallbackShader = SKShader.CreateLinearGradient(
                    new SKPoint(0f, 0f),
                    new SKPoint(0f, h),
                    colors,
                    FallbackStops,
                    SKShaderTileMode.Clamp);
                _cachedFallbackKey = key;
                _cachedFallbackHeight = h;
            }

            paint.Shader = _cachedFallbackShader;
            paint.Color = new SKColor(255, 255, 255, effectiveAlpha);
            canvas.DrawRect(0f, 0f, w, h, paint);
        }

        private void DisposeShaders()
        {
            _cachedFrameImage?.Dispose();
            _cachedFrameImage = null;
            _shaderSurface?.Dispose();
            _shaderSurface = null;
            _surfaceGrContext = null;
            _cachedShader?.Dispose();
            _cachedShader = null;
            _cachedFallbackShader?.Dispose();
            _cachedFallbackShader = null;
            _shaderUniforms?.Dispose();
            _shaderUniforms = null;
            _boundEffect = null;
            _cachedFallbackHeight = -1f;
            _dirty = true;
        }
    }

    public static readonly StyledProperty<Color?> BaseColorProperty =
        AvaloniaProperty.Register<AmbientTitleBarBleed, Color?>(nameof(BaseColor));

    public static readonly StyledProperty<double> BleedOpacityProperty =
        AvaloniaProperty.Register<AmbientTitleBarBleed, double>(nameof(BleedOpacity), 1.0);

    public Color? BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public double BleedOpacity
    {
        get => GetValue(BleedOpacityProperty);
        set => SetValue(BleedOpacityProperty, value);
    }

    private readonly Action _globalConfigChangedHandler;
    private readonly Lock _stateLock = new();
    private CompositionCustomVisual? _visual;
    private TitleBarStateSnapshot _snapshot;
    private Color? _cachedBaseColor;
    private SKColor _colorLow;
    private SKColor _colorMid;
    private SKColor _colorHigh;
    private bool _isAttached;
    private bool _syncQueued;

    public AmbientTitleBarBleed()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _globalConfigChangedHandler = PushStateToHandler;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AmbientBackdrop.GlobalConfigChanged += _globalConfigChangedHandler;
        EnsureVisual(new Vector(Bounds.Width, Bounds.Height));
        PushStateToHandler();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        AmbientBackdrop.GlobalConfigChanged -= _globalConfigChangedHandler;

        if (_visual != null)
        {
            _visual.SendHandlerMessage(MsgStop);
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }

        lock (_stateLock)
        {
            _syncQueued = false;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Vector(finalSize.Width, finalSize.Height);
        bool created = EnsureVisual(size);
        if (_visual != null && (_visual.Size != size || created))
        {
            _visual.Size = size;
            PushStateToHandler();
        }
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BaseColorProperty || change.Property == BleedOpacityProperty)
            PushStateToHandler();
    }

    private bool EnsureVisual(Vector size)
    {
        if (!_isAttached || _visual != null)
            return false;

        if (ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor)
            return false;

        _visual = compositor.CreateCustomVisual(new TitleBarBleedHandler(this));
        _visual.Size = size;
        ElementComposition.SetElementChildVisual(this, _visual);
        return true;
    }

    private void PushStateToHandler()
    {
        if (!_isAttached) return;

        EnsureVisual(new Vector(Bounds.Width, Bounds.Height));
        if (_visual == null) return;

        var mode = AmbientBackdrop.CurrentMode;
        var baseColor = BaseColor;
        double opacity = Math.Clamp(BleedOpacity, 0.0, 1.0);
        bool enabled = baseColor.HasValue && mode != AmbientBackdropMode.Off && opacity > 0.005;

        if (enabled && _cachedBaseColor != baseColor!.Value)
        {
            _cachedBaseColor = baseColor.Value;
            AmbientBackdrop.BuildPaletteColors(baseColor.Value, out _colorLow, out _colorMid, out _colorHigh);
        }

        _visual.Opacity = enabled ? 1f : 0f;

        uint midKey = ((uint)_colorMid.Red << 16) | ((uint)_colorMid.Green << 8) | _colorMid.Blue;
        bool shouldSend = false;

        lock (_stateLock)
        {
            _snapshot = new TitleBarStateSnapshot
            {
                IsEnabled = enabled,
                Mode = mode,
                GlobalIntensity = AmbientBackdrop.CurrentIntensity,
                Reactivity = AmbientBackdrop.CurrentReactivity,
                BleedOpacity = opacity,
                ColorLow = _colorLow,
                ColorMid = _colorMid,
                ColorHigh = _colorHigh,
                ColorMidKey = midKey
            };

            if (!_syncQueued)
            {
                _syncQueued = true;
                shouldSend = true;
            }
        }

        if (shouldSend)
            _visual.SendHandlerMessage(MsgSyncState);
    }
}