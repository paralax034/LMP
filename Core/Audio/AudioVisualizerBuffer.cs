using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio;

/// <summary>
/// Immutable zero-allocation snapshot of instantaneous audio visualization features
/// published by the audio thread and consumed by UI/render controls in a single read.
/// </summary>
public readonly record struct AudioVisualizerSnapshot(
    bool IsActive,
    float Low,
    float Mid,
    float High,
    float Beat,
    float Overall,
    float Vocal,
    float BassFlux,
    float MidFlux,
    float HighFlux,
    float SpectralCentroid,
    int DominantBand,
    float MacroEnergy = 0f,
    float DropImpact = 0f,
    float HarmonicEnergy = 0f,
    float PercussiveEnergy = 0f,
    float SnareFlux = 0f);

/// <summary>
/// Thread-safe lock-free buffer for synchronizing instantaneous signal levels, macro phrase dynamics,
/// harmonic/percussive separation, SuperFlux onsets, vocal presence, and 10 ISO bands with 0 B allocations.
/// </summary>
/// <remarks>
/// Reader lease state is isolated in a separate static container to avoid false sharing with audio-written fields,
/// and lease timestamps are refreshed at most once per <see cref="LeaseRefreshIntervalMs"/> ms.
/// </remarks>
public static class AudioVisualizerBuffer
{
    [InlineArray(10)]
    private struct BandLevels10
    {
        private float _element0;
    }

    /// <summary>
    /// Isolated static holder for reader lease timestamp so render/UI lease extensions
    /// do not share a 64-byte cache line with audio-thread writes.
    /// </summary>
    private static class ReaderLeaseState
    {
        public static long LastReadTimestamp;
    }

    /// <summary>
    /// Spectrum data staleness threshold (45 ms in Stopwatch ticks).
    /// If WASAPI is paused, awaiting network, or playing sustained digital silence, reading returns inactive/0f.
    /// </summary>
    private static readonly long StaleThresholdTicks = Stopwatch.Frequency * 45 / 1000;

    /// <summary>
    /// Reader lease duration in milliseconds: double the idle polling interval (250 ms) in <c>EqualizerCurveControl</c>,
    /// ensuring a single missed UI tick does not disable analysis.
    /// </summary>
    internal const int ReaderLeaseMs = 500;

    private const int LeaseRefreshIntervalMs = 100;
    private const float AudibleOverallThreshold = 1e-4f;

    private static readonly long ReaderLeaseTicks = Stopwatch.Frequency * ReaderLeaseMs / 1000;
    private static readonly long LeaseRefreshTicks = Stopwatch.Frequency * LeaseRefreshIntervalMs / 1000;

    private static long _seqVersion;
    private static long _lastUpdateTimestamp;
    private static float _lowLevel;
    private static float _midLevel;
    private static float _highLevel;
    private static float _beatPulse;
    private static float _overallLevel;
    private static float _vocalPresence;
    private static float _bassFlux;
    private static float _midFlux;
    private static float _highFlux;
    private static float _spectralCentroid;
    private static float _macroEnergy;
    private static float _dropImpact;
    private static float _harmonicEnergy;
    private static float _percussiveEnergy;
    private static float _snareFlux;
    private static int _dominantBandIndex;
    private static BandLevels10 _bands10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TouchReaderLease(long now)
    {
        long prev = Volatile.Read(ref ReaderLeaseState.LastReadTimestamp);
        if (prev == 0 || (now - prev) >= LeaseRefreshTicks)
            Volatile.Write(ref ReaderLeaseState.LastReadTimestamp, now);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsActive(out long now)
    {
        now = Stopwatch.GetTimestamp();
        TouchReaderLease(now);

        long last = Volatile.Read(ref _lastUpdateTimestamp);
        return last != 0 && (now - last) <= StaleThresholdTicks;
    }

    /// <summary>
    /// Returns true if fresh audible audio frames were received within the last 45 ms.
    /// Extends the reader lease, so UI idle polling automatically resumes spectrum analysis without deadlocks.
    /// </summary>
    public static bool HasRecentActivity => IsActive(out _);

    /// <summary>
    /// Returns true if levels were read by anyone within the last <see cref="ReaderLeaseMs"/> ms.
    /// Intended for the audio stream and does not extend the lease itself.
    /// </summary>
    internal static bool HasActiveReaders
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long last = Volatile.Read(ref ReaderLeaseState.LastReadTimestamp);
            return last != 0 && (Stopwatch.GetTimestamp() - last) <= ReaderLeaseTicks;
        }
    }

    public static float LowLevel => IsActive(out _) ? Volatile.Read(ref _lowLevel) : 0f;
    public static float MidLevel => IsActive(out _) ? Volatile.Read(ref _midLevel) : 0f;
    public static float HighLevel => IsActive(out _) ? Volatile.Read(ref _highLevel) : 0f;
    public static float BeatPulse => IsActive(out _) ? Volatile.Read(ref _beatPulse) : 0f;
    public static float OverallLevel => IsActive(out _) ? Volatile.Read(ref _overallLevel) : 0f;
    public static float VocalPresence => IsActive(out _) ? Volatile.Read(ref _vocalPresence) : 0f;
    public static float SpectralCentroid => IsActive(out _) ? Volatile.Read(ref _spectralCentroid) : 0f;
    public static float MacroEnergy => IsActive(out _) ? Volatile.Read(ref _macroEnergy) : 0f;
    public static float DropImpact => IsActive(out _) ? Volatile.Read(ref _dropImpact) : 0f;
    public static float HarmonicEnergy => IsActive(out _) ? Volatile.Read(ref _harmonicEnergy) : 0f;
    public static float PercussiveEnergy => IsActive(out _) ? Volatile.Read(ref _percussiveEnergy) : 0f;

    /// <summary>Index of the dominant ISO band [0..9]; reading extends the reader lease.</summary>
    public static int DominantBandIndex
    {
        get
        {
            TouchReaderLease(Stopwatch.GetTimestamp());
            return Volatile.Read(ref _dominantBandIndex);
        }
    }

    /// <summary>
    /// Atomically reads a consistent snapshot of all musical visualization features with 1 timestamp read and 0 allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadSnapshot(out AudioVisualizerSnapshot snapshot)
    {
        if (!IsActive(out _))
        {
            snapshot = default;
            return false;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            long v0 = Volatile.Read(ref _seqVersion);
            if ((v0 & 1L) != 0)
                continue;

            float low = _lowLevel;
            float mid = _midLevel;
            float high = _highLevel;
            float beat = _beatPulse;
            float overall = _overallLevel;
            float vocal = _vocalPresence;
            float bassFlux = _bassFlux;
            float midFlux = _midFlux;
            float highFlux = _highFlux;
            float centroid = _spectralCentroid;
            float macro = _macroEnergy;
            float drop = _dropImpact;
            float harmonic = _harmonicEnergy;
            float percussive = _percussiveEnergy;
            float snare = _snareFlux;
            int dominant = _dominantBandIndex;

            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _seqVersion) == v0)
            {
                snapshot = new AudioVisualizerSnapshot(
                    IsActive: true,
                    Low: low,
                    Mid: mid,
                    High: high,
                    Beat: beat,
                    Overall: overall,
                    Vocal: vocal,
                    BassFlux: bassFlux,
                    MidFlux: midFlux,
                    HighFlux: highFlux,
                    SpectralCentroid: centroid,
                    DominantBand: dominant,
                    MacroEnergy: macro,
                    DropImpact: drop,
                    HarmonicEnergy: harmonic,
                    PercussiveEnergy: percussive,
                    SnareFlux: snare);
                return true;
            }
        }

        snapshot = new AudioVisualizerSnapshot(
            IsActive: true,
            Low: Volatile.Read(ref _lowLevel),
            Mid: Volatile.Read(ref _midLevel),
            High: Volatile.Read(ref _highLevel),
            Beat: Volatile.Read(ref _beatPulse),
            Overall: Volatile.Read(ref _overallLevel),
            Vocal: Volatile.Read(ref _vocalPresence),
            BassFlux: Volatile.Read(ref _bassFlux),
            MidFlux: Volatile.Read(ref _midFlux),
            HighFlux: Volatile.Read(ref _highFlux),
            SpectralCentroid: Volatile.Read(ref _spectralCentroid),
            DominantBand: Volatile.Read(ref _dominantBandIndex),
            MacroEnergy: Volatile.Read(ref _macroEnergy),
            DropImpact: Volatile.Read(ref _dropImpact),
            HarmonicEnergy: Volatile.Read(ref _harmonicEnergy),
            PercussiveEnergy: Volatile.Read(ref _percussiveEnergy),
            SnareFlux: Volatile.Read(ref _snareFlux));
        return true;
    }

    /// <summary>
    /// Reads current levels of 10 ISO bands [0..1] into the provided Span with zero allocations.
    /// Automatically zeroes out the bands if the playback stream is paused, seeking, or silent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyBands10(Span<float> destination)
    {
        int count = Math.Min(10, destination.Length);
        if (!IsActive(out _))
        {
            destination[..count].Clear();
            return;
        }

        for (int i = 0; i < count; i++)
            destination[i] = Volatile.Read(ref _bands10[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Update10Band(
        ReadOnlySpan<float> bands10,
        float low,
        float mid,
        float high,
        float beat,
        float overall,
        int dominantBand) =>
        Update10Band(
            bands10,
            low,
            mid,
            high,
            beat,
            overall,
            vocal: 0f,
            bassFlux: 0f,
            midFlux: 0f,
            highFlux: 0f,
            spectralCentroid: dominantBand / 9f,
            dominantBand,
            macroEnergy: overall,
            dropImpact: 0f,
            harmonicEnergy: 0f,
            percussiveEnergy: beat,
            snareFlux: 0f);

    /// <summary>Exposes the current SeqLock sequence counter for concurrency diagnostics.</summary>
    internal static long CurrentSeqVersion => Volatile.Read(ref _seqVersion);

    /// <summary>
    /// Atomically acquires the odd SeqLock write state across concurrent publishers
    /// (WASAPI callback vs actor-thread reset) without ever sleeping on the audio thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AcquireWriteSeq()
    {
        var spin = new SpinWait();
        while (true)
        {
            long seq = Volatile.Read(ref _seqVersion);
            if ((seq & 1L) == 0 && Interlocked.CompareExchange(ref _seqVersion, seq + 1, seq) == seq)
                return seq;

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Update10Band(
        ReadOnlySpan<float> bands10,
        float low,
        float mid,
        float high,
        float beat,
        float overall,
        float vocal,
        float bassFlux,
        float midFlux,
        float highFlux,
        float spectralCentroid,
        int dominantBand,
        float macroEnergy = 0f,
        float dropImpact = 0f,
        float harmonicEnergy = 0f,
        float percussiveEnergy = 0f,
        float snareFlux = 0f)
    {
        long seq = AcquireWriteSeq();

        int count = Math.Min(10, bands10.Length);
        for (int i = 0; i < count; i++)
            _bands10[i] = bands10[i];

        _lowLevel = low;
        _midLevel = mid;
        _highLevel = high;
        _beatPulse = beat;
        _overallLevel = overall;
        _vocalPresence = vocal;
        _bassFlux = bassFlux;
        _midFlux = midFlux;
        _highFlux = highFlux;
        _spectralCentroid = Math.Clamp(spectralCentroid, 0f, 1f);
        _macroEnergy = Math.Clamp(macroEnergy, 0f, 1.5f);
        _dropImpact = Math.Clamp(dropImpact, 0f, 1f);
        _harmonicEnergy = Math.Clamp(harmonicEnergy, 0f, 1f);
        _percussiveEnergy = Math.Clamp(percussiveEnergy, 0f, 1f);
        _snareFlux = Math.Clamp(snareFlux, 0f, 1f);
        _dominantBandIndex = Math.Clamp(dominantBand, 0, 9);

        // Only advance the audible activity timestamp for non-silent audio so sustained digital silence
        // naturally ages past StaleThresholdTicks and allows CompositionSilenceMonitor to sleep.
        if (overall >= AudibleOverallThreshold)
            Volatile.Write(ref _lastUpdateTimestamp, Stopwatch.GetTimestamp());

        Volatile.Write(ref _seqVersion, seq + 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reset()
    {
        long seq = AcquireWriteSeq();

        Volatile.Write(ref _lastUpdateTimestamp, 0L);

        for (int i = 0; i < 10; i++)
            _bands10[i] = 0f;

        _lowLevel = 0f;
        _midLevel = 0f;
        _highLevel = 0f;
        _beatPulse = 0f;
        _overallLevel = 0f;
        _vocalPresence = 0f;
        _bassFlux = 0f;
        _midFlux = 0f;
        _highFlux = 0f;
        _spectralCentroid = 0f;
        _macroEnergy = 0f;
        _dropImpact = 0f;
        _harmonicEnergy = 0f;
        _percussiveEnergy = 0f;
        _snareFlux = 0f;
        _dominantBandIndex = 0;

        Volatile.Write(ref _seqVersion, seq + 2);
    }
}