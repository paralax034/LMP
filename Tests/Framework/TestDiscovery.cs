using System.Collections.Frozen;
using System.Reflection;

namespace LMP.Tests.Framework;

/// <summary>
/// Автоматическое обнаружение тестов через рефлексию.
/// Кэширует дескрипторы, отсортированные списки и группы один раз за время жизни процесса.
/// Потокобезопасен, не производит повторных аллокаций при открытии UI.
/// </summary>
public static class TestDiscovery
{
    private static FrozenDictionary<string, TestDescriptor>? _cache;
    private static TestDescriptor[]? _orderedCache;
    private static FrozenSet<string>? _groupsCache;
    private static readonly Lock _lock = new();

    /// <summary>Все обнаруженные тесты по ID.</summary>
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

    /// <summary>Тесты, отсортированные по категории → группе → порядку (кэшированный массив).</summary>
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

    /// <summary>Тесты, сгруппированные по категории.</summary>
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
    /// Возвращает все уникальные тематические группы тестов.
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

    /// <summary>Сбрасывает кэш тестов.</summary>
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
        var assembly = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, TestDescriptor>(64, StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) continue;
            if (type.Namespace is null || !type.Namespace.StartsWith("LMP.Tests", StringComparison.Ordinal)) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<TestMethodAttribute>();
                if (attr is null) continue;

                if (method.ReturnType != typeof(Task))
                {
                    Log.Warn($"[TestDiscovery] Skipping {type.Name}.{method.Name}: return type must be Task, got {method.ReturnType.Name}");
                    continue;
                }

                var parameters = method.GetParameters();
                TestParameterSignature signature;

                switch (parameters.Length)
                {
                    case 0:
                        signature = TestParameterSignature.None;
                        break;
                    case 1 when parameters[0].ParameterType == typeof(IServiceProvider):
                        signature = TestParameterSignature.ServicesOnly;
                        break;
                    case 1 when parameters[0].ParameterType == typeof(CancellationToken):
                        signature = TestParameterSignature.TokenOnly;
                        break;
                    case 2 when parameters[0].ParameterType == typeof(IServiceProvider) && parameters[1].ParameterType == typeof(CancellationToken):
                        signature = TestParameterSignature.ServicesAndToken;
                        break;
                    case 2 when parameters[0].ParameterType == typeof(CancellationToken) && parameters[1].ParameterType == typeof(IServiceProvider):
                        signature = TestParameterSignature.TokenAndServices;
                        break;
                    default:
                        Log.Warn($"[TestDiscovery] Skipping {type.Name}.{method.Name}: unsupported parameters count ({parameters.Length})");
                        continue;
                }

                var className = type.Name;
                var displayName = attr.DisplayName ?? FormatMethodName(method.Name);
                var id = $"{className}.{method.Name}";

                var rawGroup = attr.Group ?? InferGroupFromClassName(className);
                var group = TestGroups.Normalize(rawGroup);

                result[id] = new TestDescriptor
                {
                    Id = id,
                    DisplayName = displayName,
                    Category = attr.Category,
                    Group = group,
                    ClassName = className,
                    Order = attr.Order,
                    RequiresNetwork = attr.RequiresNetwork,
                    TimeoutSeconds = attr.TimeoutSeconds,
                    Signature = signature,
                    Method = method,
                };
            }
        }

        Log.Info($"[TestDiscovery] Found {result.Count} tests in {assembly.GetName().Name}");
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