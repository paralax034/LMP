using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Runtime blacklist of CDN hosts blocked by TSPU.
/// Transient state — not persisted to disk.
/// Entries automatically expire by TTL (TSPU may change rules).
/// </summary>
/// <remarks>
/// Thread-safe. <see cref="IsBlocked"/> check is O(1), zero-alloc
/// (except for lazy removal of expired entries).
/// Separate from <see cref="CdnHostStatsStore"/> — this is not statistics,
/// but an operational blacklist for CDN failover.
/// </remarks>
internal sealed class CdnBlacklist
{
    private readonly ConcurrentDictionary<string, long> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _ttlTicks;

    /// <summary>Creates a blacklist with the specified TTL for entries.</summary>
    /// <param name="ttl">Entry time-to-live. Defaults to 5 minutes.</param>
    internal CdnBlacklist(TimeSpan? ttl = null)
    {
        _ttlTicks = (ttl ?? TimeSpan.FromMinutes(5)).Ticks;
    }

    /// <summary>Number of entries (including expired before lazy cleanup).</summary>
    internal int Count => _blocked.Count;

    /// <summary>Marks a CDN host as blocked.</summary>
    internal void MarkBlocked(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _blocked[host] = DateTime.UtcNow.Ticks;
        Log.Info($"[CdnBlacklist] Host blocked: {host}");
    }

    /// <summary>Checks if a host is blocked (taking TTL into account).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsBlocked(string host)
    {
        if (!_blocked.TryGetValue(host, out long blockedAtTicks))
            return false;

        if (DateTime.UtcNow.Ticks - blockedAtTicks > _ttlTicks)
        {
            _blocked.TryRemove(host, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the host from the URL is blocked.
    /// Fast parsing without <see cref="Uri"/> allocation.
    /// </summary>
    internal bool IsBlockedUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || _blocked.IsEmpty)
            return false;

        ReadOnlySpan<char> span = url.AsSpan();
        int schemeEnd = span.IndexOf("://".AsSpan());
        if (schemeEnd < 0) return false;

        var afterScheme = span[(schemeEnd + 3)..];
        int hostEnd = afterScheme.IndexOfAny('/', ':');
        var host = hostEnd >= 0 ? afterScheme[..hostEnd] : afterScheme;

        // ToString() is unavoidable for Dictionary lookup
        return IsBlocked(host.ToString());
    }

    /// <summary>Unblocks a host.</summary>
    internal void Unblock(string host)
    {
        if (_blocked.TryRemove(host, out _))
            Log.Info($"[CdnBlacklist] Host unblocked: {host}");
    }

    /// <summary>Clears all entries.</summary>
    internal void Clear()
    {
        int count = _blocked.Count;
        _blocked.Clear();
        if (count > 0)
            Log.Debug($"[CdnBlacklist] Cleared {count} entries");
    }

    /// <summary>Returns all active (non-expired) blocked hosts.</summary>
    internal IReadOnlyList<string> GetBlockedHosts()
    {
        long now = DateTime.UtcNow.Ticks;
        var result = new List<string>();

        foreach (var kvp in _blocked)
        {
            if (now - kvp.Value <= _ttlTicks)
                result.Add(kvp.Key);
        }

        return result;
    }
}