using System.Collections.Frozen;

namespace LMP.Tests.Framework;

/// <summary>
/// Discovers tests via the registry built by LMP.Tests.Generator at build time: reflection-free and AOT-compatible.
/// Caches descriptors, sorted lists, and groups once per process lifetime.
/// Thread-safe, performs no re-allocations when UI opens.
/// </summary>
public static class TestDiscovery
{
    private static FrozenDictionary<string, TestDescriptor>? _cache;
    private static TestDescriptor[]? _orderedCache;
    private static FrozenSet<string>? _groupsCache;
    private static readonly Lock _lock = new();

    /// <summary>All discovered tests by ID.</summary>
    public static FrozenDictionary<string, TestDescriptor> GetAll()
    {
        var cache = _cache;
        if (cache is not null) return cache;

        lock (_lock)
        {
            EnsureDiscoveredUnsafe();
            return _cache!;
        }
    }

    /// <summary>Tests sorted by category → group → order (cached array).</summary>
    public static IReadOnlyList<TestDescriptor> GetAllOrdered()
    {
        var ordered = _orderedCache;
        if (ordered is not null) return ordered;

        lock (_lock)
        {
            EnsureDiscoveredUnsafe();
            return _orderedCache!;
        }
    }

    /// <summary>Tests grouped by category.</summary>
    public static IReadOnlyDictionary<TestCategory, IReadOnlyList<TestDescriptor>> GetGrouped()
    {
        var ordered = GetAllOrdered();
        var dict = new Dictionary<TestCategory, List<TestDescriptor>>(6);

        for (int i = 0; i < ordered.Count; i++)
        {
            var desc = ordered[i];
            if (!dict.TryGetValue(desc.Category, out var list))
            {
                list = [];
                dict[desc.Category] = list;
            }
            list.Add(desc);
        }

        var result = new Dictionary<TestCategory, IReadOnlyList<TestDescriptor>>(dict.Count);
        foreach (var (k, v) in dict)
            result[k] = v;

        return result;
    }

    /// <summary>
    /// Returns all unique topic groups of tests.
    /// </summary>
    public static FrozenSet<string> GetAllGroups()
    {
        var groups = _groupsCache;
        if (groups is not null) return groups;

        lock (_lock)
        {
            EnsureDiscoveredUnsafe();
            return _groupsCache!;
        }
    }

    /// <summary>Resets the test cache.</summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _orderedCache = null;
            _groupsCache = null;
        }
    }

    private static void EnsureDiscoveredUnsafe()
    {
        if (_cache is not null && _orderedCache is not null && _groupsCache is not null)
            return;

        var discovered = Discover();
        var ordered = new TestDescriptor[discovered.Count];
        int idx = 0;
        var groupSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var desc in discovered.Values)
        {
            ordered[idx++] = desc;
            groupSet.Add(desc.Group);
        }

        Array.Sort(ordered, static (a, b) =>
        {
            int cmp = a.Category.CompareTo(b.Category);
            if (cmp != 0) return cmp;

            cmp = StringComparer.Ordinal.Compare(a.Group, b.Group);
            if (cmp != 0) return cmp;

            cmp = a.Order.CompareTo(b.Order);
            if (cmp != 0) return cmp;

            return StringComparer.Ordinal.Compare(a.DisplayName, b.DisplayName);
        });

        _orderedCache = ordered;
        _groupsCache = groupSet.ToFrozenSet(StringComparer.Ordinal);
        _cache = discovered;
    }

    private static FrozenDictionary<string, TestDescriptor> Discover()
    {
        var registrations = GeneratedTestRegistry.Create();
        var result = new Dictionary<string, TestDescriptor>(registrations.Length, StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            var attr = registration.Attribute;
            var className = registration.ClassName;
            var id = $"{className}.{registration.MethodName}";

            result[id] = new TestDescriptor
            {
                Id = id,
                DisplayName = attr.DisplayName ?? FormatMethodName(registration.MethodName),
                Category = attr.Category,
                Group = TestGroups.Normalize(attr.Group ?? InferGroupFromClassName(className)),
                ClassName = className,
                Order = attr.Order,
                RequiresNetwork = attr.RequiresNetwork,
                TimeoutSeconds = attr.TimeoutSeconds,
                Signature = registration.Signature,
                Invoke = registration.Invoke,
            };
        }

        Log.Info($"[TestDiscovery] Found {result.Count} tests in compile-time registry");
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string InferGroupFromClassName(string className)
    {
        var name = className.AsSpan();

        if (name.EndsWith("Tests", StringComparison.Ordinal))
            name = name[..^5];
        else if (name.EndsWith("Benchmarks", StringComparison.Ordinal))
            name = name[..^10];

        return name switch
        {
            "SigCipherSolver" or "SigCipher" or "NToken" or "PoToken" => TestGroups.Cipher,
            "CdnDiagnostic" or "StreamFreshness" => TestGroups.Network,
            "StreamPipeline" or "PlayerResponse" or "SearchResponse" or "PlaylistStreamingParser" or "StreamingMemoryBenchmark" => TestGroups.Youtube,
            "Cache" or "AtomicFile" or "MemoryPack" => TestGroups.Storage,
            "AudioHotPath" or "HotPathAllocation" or "PreScan" => TestGroups.Audio,
            "PlaylistOrder" => TestGroups.Playlists,
            "Notification" or "Update" => TestGroups.System,
            _ => TestGroups.Normalize(name.ToString()),
        };
    }

    private static string FormatMethodName(string methodName)
    {
        var name = methodName.AsSpan();

        if (name.StartsWith("Test", StringComparison.Ordinal))
            name = name[4..];

        if (name.EndsWith("Async", StringComparison.Ordinal))
            name = name[..^5];

        Span<char> buffer = stackalloc char[name.Length * 2];
        int pos = 0;

        for (int i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(name[i - 1]))
                buffer[pos++] = ' ';
            buffer[pos++] = ch;
        }

        return new string(buffer[..pos]);
    }
}