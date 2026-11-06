using System.Text.Json;
using MemoryPack;

namespace LMP.Core.Audio.Http;

// Public Models

/// <summary>
/// Statistics for a single YouTube CDN cluster.
/// Cluster = group of ingress nodes sharing the same <c>sn-XXXXXXXX</c> suffix.
/// </summary>
[MemoryPackable]
public sealed partial class CdnClusterStats
{
    /// <summary>
    /// Cluster identifier extracted from the hostname.
    /// Example: <c>sn-4g5ednle</c> from <c>rrsn-4g5ednle.googlevideo.com</c>.
    /// </summary>
    public required string ClusterId { get; set; }

    /// <summary>
    /// Last known alive ingress host of the cluster.
    /// Used as a target for <c>generate_204</c>.
    /// </summary>
    public required string SampleHost { get; set; }

    /// <summary>
    /// Total number of recorded hits to the cluster.
    /// </summary>
    public int HitCount { get; set; }

    /// <summary>
    /// Time of the last access (UTC).
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Exponentially weighted moving average (EMA) of TTFB in ms (alpha = 0.2).
    /// <c>double.NaN</c> — no measurements yet.
    /// </summary>
    public double AvgTtfbMs { get; set; } = double.NaN;
}

/// <summary>
/// Envelope for the JSON file containing CDN cluster statistics.
/// </summary>
[MemoryPackable]
public sealed partial class CdnHostStatsEnvelope
{
    /// <summary>Statistics for known CDN clusters.</summary>
    public List<CdnClusterStats> Clusters { get; set; } = [];

    /// <summary>
    /// Accumulated total hit count across all clusters.
    /// Used to calculate the adaptive threshold.
    /// </summary>
    public int TotalHits { get; set; }

    /// <summary>Time of the last disk save (UTC).</summary>
    public DateTime LastSavedUtc { get; set; }
}

// Store

/// <summary>
/// Persistent storage for YouTube CDN cluster statistics.
/// <para>
/// Used in two ways:
/// </para>
/// <list type="number">
///   <item>
///     <b>Startup warmup:</b> <see cref="PreWarmTopClustersAsync"/> warms up
///     the top-N clusters by score right after application startup — before
///     the user clicks Play.
///   </item>
///   <item>
///     <b>Adaptive timeout:</b> <see cref="GetAvgTtfbMs"/> returns EMA TTFB
///     for a cluster so that probe-timeout in <see cref="SessionCacheStore"/>
///     is proportional to the actual CDN latency.
///   </item>
/// </list>
/// <para>
/// Data is stored in <see cref="G.FilePath.CdnHostStats"/>.
/// Disk writes are deferred (dirty flag + 5 min timer + graceful shutdown).
/// </para>
/// </summary>
internal static class CdnHostStatsStore
{
    private const int MaxClusters = 12;
    private const int ClusterEvictionDays = 30;
    private const double EmaAlpha = 0.2;
    private const int SaveIntervalMinutes = 5;

    private static readonly Lock _lock = new();
    private static CdnHostStatsEnvelope _data = new();
    private static bool _dirty;
    private static DateTime _lastSaveTime = DateTime.MinValue;

    // Load / Save

    /// <summary>
    /// Loads statistics from disk. Called once during application startup.
    /// Safe against missing files or corrupt JSON.
    /// </summary>
    public static void Load()
    {
        _lastSaveTime = DateTime.UtcNow;
        try
        {
            var binPath = G.FilePath.CdnHostStats;
            var binary = AtomicFile.ReadBytesWithFallback(binPath, out bool recovered);

            if (recovered)
                Log.Warn("[CdnHostStats] Recovered CDN host stats from backup (.bak)");

            if (binary != null && binary.Length > 0)
            {
                var envelope = MemoryPackSerializer.Deserialize<CdnHostStatsEnvelope>(binary);
                if (envelope != null)
                {
                    lock (_lock)
                    {
                        _data = envelope;
                        EvictStaleClustersMustHoldLock();
                    }

                    Log.Debug($"[CdnHostStats] Loaded {_data.Clusters.Count} cluster(s) from binary store, totalHits={_data.TotalHits}");
                    return;
                }
            }

            var legacyJsonPath = Path.ChangeExtension(binPath, ".json");
            if (File.Exists(legacyJsonPath))
            {
                MigrateLegacyJsonIfPresent(legacyJsonPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CdnHostStats] Load failed (starting fresh): {ex.Message}");
            lock (_lock) { _data = new CdnHostStatsEnvelope(); }
        }
    }

    /// <summary>
    /// Isolated migration of CDN node statistics from legacy JSON to binary MemoryPack.
    /// </summary>
    private static void MigrateLegacyJsonIfPresent(string legacyJsonPath)
    {
        var json = AtomicFile.ReadTextWithFallback(legacyJsonPath, out bool jsonRecovered);
        if (string.IsNullOrWhiteSpace(json))
            return;

        if (jsonRecovered)
            Log.Warn("[CdnHostStats] Recovered CDN host stats from backup (.bak)");

        var legacyEnvelope = JsonSerializer.Deserialize(
            json,
            AppJsonContext.DefaultCompact.CdnHostStatsEnvelope);

        if (legacyEnvelope is null)
            return;

        lock (_lock)
        {
            _data = legacyEnvelope;
            EvictStaleClustersMustHoldLock();
            _dirty = true;
        }

        Save();

        try
        {
            var legacyBak = legacyJsonPath + ".bak";
            if (File.Exists(legacyJsonPath) && !File.Exists(legacyBak))
                File.Copy(legacyJsonPath, legacyBak, overwrite: true);

            if (File.Exists(legacyJsonPath))
                File.Delete(legacyJsonPath);
        }
        catch { }

        Log.Info($"[CdnHostStats] Migrated {legacyEnvelope.Clusters.Count} cluster(s) from legacy JSON to MemoryPack");
    }

    /// <summary>
    /// Saves statistics to disk. Called by timer and during graceful shutdown.
    /// </summary>
    public static void Save()
    {
        CdnHostStatsEnvelope snapshot;

        lock (_lock)
        {
            if (!_dirty)
                return;

            _data.LastSavedUtc = DateTime.UtcNow;
            snapshot = _data;
            _dirty = false;
            _lastSaveTime = DateTime.UtcNow;
        }

        try
        {
            byte[] binary = MemoryPackSerializer.Serialize(snapshot);
            AtomicFile.WriteBytes(G.FilePath.CdnHostStats, binary, createBackup: false);
            Log.Debug($"[CdnHostStats] Saved {snapshot.Clusters.Count} cluster(s) [binary]");
        }
        catch (Exception ex)
        {
            Log.Warn($"[CdnHostStats] Save failed: {ex.Message}");
            lock (_lock) { _dirty = true; }
        }
    }

    // Record

    /// <summary>
    /// Registers a successful request to a CDN host.
    /// <para>
    /// Zero-alloc on hot path: only Lock + List search (≤ 12 elements) + int increment.
    /// Extracting clusterId occurs BEFORE entering Lock.
    /// </para>
    /// </summary>
    /// <param name="host">CDN node hostname (<c>rrsn-xxx.googlevideo.com</c>).</param>
    public static void RecordHit(string host)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return;

        lock (_lock)
        {
            var cluster = FindClusterMustHoldLock(clusterId);
            if (cluster is not null)
            {
                cluster.HitCount++;
                cluster.LastSeenUtc = DateTime.UtcNow;
                cluster.SampleHost = host;
            }
            else
            {
                if (_data.Clusters.Count >= MaxClusters)
                    EvictLowestScoreClusterMustHoldLock();

                _data.Clusters.Add(new CdnClusterStats
                {
                    ClusterId = clusterId,
                    SampleHost = host,
                    HitCount = 1,
                    LastSeenUtc = DateTime.UtcNow
                });
            }

            _data.TotalHits++;
            MarkDirtyMustHoldLock();
        }
    }

    /// <summary>
    /// Updates EMA TTFB for a cluster after a successful warmup.
    /// </summary>
    /// <param name="host">CDN node hostname.</param>
    /// <param name="ttfbMs">Measured TTFB in ms.</param>
    public static void RecordTtfb(string host, double ttfbMs)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return;

        lock (_lock)
        {
            var cluster = FindClusterMustHoldLock(clusterId);
            if (cluster is null)
                return;

            cluster.AvgTtfbMs = double.IsNaN(cluster.AvgTtfbMs)
                ? ttfbMs
                : cluster.AvgTtfbMs * (1.0 - EmaAlpha) + ttfbMs * EmaAlpha;

            MarkDirtyMustHoldLock();
        }

        FlushIfNeeded();
    }

    /// <summary>
    /// Records a failure or timeout accessing a CDN cluster and penalizes its score.
    /// Prevents dead or TSPU-blocked clusters from cycling endlessly in top startup warmup.
    /// </summary>
    /// <param name="host">CDN node hostname.</param>
    public static void RecordFailure(string host)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return;

        lock (_lock)
        {
            var cluster = FindClusterMustHoldLock(clusterId);
            if (cluster is null)
                return;

            // Slash the accumulated score and push LastSeen back to force the node out of top
            cluster.HitCount = Math.Max(0, cluster.HitCount / 2);
            cluster.LastSeenUtc = cluster.LastSeenUtc.AddDays(-14);
            cluster.AvgTtfbMs = double.NaN;

            MarkDirtyMustHoldLock();
        }

        FlushIfNeeded();
    }


    // Query

    /// <summary>
    /// Returns EMA TTFB for the cluster to which the host belongs.
    /// </summary>
    /// <param name="host">CDN node hostname.</param>
    /// <returns>EMA TTFB in ms, or <c>double.NaN</c> if no data is available.</returns>
    public static double GetAvgTtfbMs(string host)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return double.NaN;

        lock (_lock)
        {
            return FindClusterMustHoldLock(clusterId)?.AvgTtfbMs ?? double.NaN;
        }
    }

    // Startup Warmup

    /// <summary>
    /// Warms up top-N CDN clusters at application startup.
    /// <para>
    /// Ranked by score = <c>hitCount × 0.95^daysSinceLastSeen</c>.
    /// Adaptive minimum hit threshold depends on <see cref="CdnHostStatsEnvelope.TotalHits"/>:
    /// </para>
    /// <list type="table">
    ///   <item><term>totalHits &lt; 30</term><description>threshold = 3, max 1 cluster</description></item>
    ///   <item><term>30 ≤ totalHits &lt; 200</term><description>threshold = 10, max 2 clusters</description></item>
    ///   <item><term>totalHits ≥ 200</term><description>threshold = max(10, total/50), max 3 clusters</description></item>
    /// </list>
    /// <para>Fire-and-forget: errors are not propagated, warmup is best-effort.</para>
    /// </summary>
    /// <param name="httpClient">HTTP client sharing a connection pool.</param>
    /// <param name="ct">Cancellation token (application lifetime).</param>
    public static async Task PreWarmTopClustersAsync(HttpClient httpClient, CancellationToken ct)
    {
        string[] targets;

        lock (_lock)
        {
            if (_data.Clusters.Count == 0)
                return;

            var (minHits, maxClusters) = ComputeAdaptivePolicy(_data.TotalHits);

            var now = DateTime.UtcNow;
            int count = 0;
            var buffer = new string[Math.Min(maxClusters, _data.Clusters.Count)];

            // Allocation-free sort: insertion sort by score (N ≤ 12)
            Span<(double Score, int Index)> scored = stackalloc (double, int)[_data.Clusters.Count];
            int scoredCount = 0;

            for (int i = 0; i < _data.Clusters.Count; i++)
            {
                var c = _data.Clusters[i];
                if (c.HitCount < minHits)
                    continue;

                double days = (now - c.LastSeenUtc).TotalDays;
                double score = c.HitCount * Math.Pow(0.95, days);
                scored[scoredCount++] = (score, i);
            }

            for (int i = 0; i < scoredCount && count < maxClusters; i++)
            {
                int best = i;
                for (int j = i + 1; j < scoredCount; j++)
                {
                    if (scored[j].Score > scored[best].Score)
                        best = j;
                }

                if (best != i)
                    (scored[i], scored[best]) = (scored[best], scored[i]);

                buffer[count++] = _data.Clusters[scored[i].Index].SampleHost;
            }

            targets = buffer[..count];
        }

        if (targets.Length == 0)
            return;

        Log.Debug($"[CdnHostStats] Startup warmup: {targets.Length} cluster(s)");

        for (int i = 0; i < targets.Length; i++)
            _ = CdnConnectionPreWarmer.WarmSingleHostAsync(httpClient, targets[i], ct);

    }

    // Periodic Save

    /// <summary>
    /// Saves data to disk if enough time has passed since the last save.
    /// Called from <see cref="RecordHit"/> when dirty flag has been set for a long time.
    /// </summary>
    public static void FlushIfNeeded()
    {
        bool shouldSave;
        lock (_lock)
        {
            shouldSave = _dirty
                && (DateTime.UtcNow - _lastSaveTime).TotalMinutes >= SaveIntervalMinutes;
        }

        if (shouldSave)
            Save();
    }

    // Private Helpers

    private static (int MinHits, int MaxClusters) ComputeAdaptivePolicy(int totalHits) =>
        totalHits switch
        {
            < 30 => (3, 1),
            < 200 => (10, 2),
            _ => (Math.Max(10, totalHits / 50), 3)
        };

    private static CdnClusterStats? FindClusterMustHoldLock(string clusterId)
    {
        for (int i = 0; i < _data.Clusters.Count; i++)
        {
            if (string.Equals(_data.Clusters[i].ClusterId, clusterId, StringComparison.Ordinal))
                return _data.Clusters[i];
        }
        return null;
    }

    private static void EvictLowestScoreClusterMustHoldLock()
    {
        if (_data.Clusters.Count == 0)
            return;

        var now = DateTime.UtcNow;
        int worstIdx = 0;
        double worstScore = double.MaxValue;

        for (int i = 0; i < _data.Clusters.Count; i++)
        {
            double days = (now - _data.Clusters[i].LastSeenUtc).TotalDays;
            double score = _data.Clusters[i].HitCount * Math.Pow(0.95, days);
            if (score < worstScore)
            {
                worstScore = score;
                worstIdx = i;
            }
        }

        _data.Clusters.RemoveAt(worstIdx);
    }

    private static void EvictStaleClustersMustHoldLock()
    {
        var cutoff = DateTime.UtcNow.AddDays(-ClusterEvictionDays);
        for (int i = _data.Clusters.Count - 1; i >= 0; i--)
        {
            if (_data.Clusters[i].LastSeenUtc < cutoff)
                _data.Clusters.RemoveAt(i);
        }
    }

    private static void MarkDirtyMustHoldLock()
    {
        _dirty = true;
    }

    /// <summary>
    /// Extracts ClusterId from CDN hostname. Zero-alloc via <see cref="ReadOnlySpan{T}"/>.
    /// </summary>
    /// <example>
    /// <c>rrsn-4g5ednle.googlevideo.com</c> → <c>sn-4g5ednle</c>
    /// </example>
    internal static string? ExtractClusterId(string host)
    {
        var span = host.AsSpan();

        var separatorIdx = span.IndexOf("---".AsSpan(), StringComparison.Ordinal);
        if (separatorIdx < 0)
            return null;

        var after = span[(separatorIdx + 3)..];

        var domainIdx = after.IndexOf(".googlevideo.com".AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (domainIdx <= 0)
            return null;

        return new string(after[..domainIdx]);
    }
}