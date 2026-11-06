using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Platform;

namespace LMP.Core.Services;

/// <summary>
/// Application localization and multilingual support service.
/// <para>
/// Optimized for AOT and zero-allocation in hot paths using <see cref="FrozenDictionary{TKey, TValue}"/>
/// and missing key caching pool.
/// </para>
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static readonly LocalizationService Instance = new();

    /// <summary>
    /// Immutable FrozenDictionary for O(1) key lookups without synchronization overhead or collision checks.
    /// </summary>
    private FrozenDictionary<string, string> _resources = FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// Pool of missing key markers formatted as [key] to prevent GC allocations on repeated requests.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _missingMarkerCache = new(StringComparer.Ordinal);

    private bool _isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    /// <summary>
    /// Gets the list of supported UI languages.
    /// </summary>
    public List<LanguageItem> AvailableLanguages { get; } =
    [
        new() { Code = "en", Name = "English" },
        new() { Code = "ru", Name = "Русский" }
    ];

    /// <summary>
    /// Gets the current UI language code.
    /// </summary>
    public string CurrentLanguageCode { get; private set; } = "en";

    /// <summary>
    /// Gets or sets the current application language. Triggers resource reload and language changed event on update.
    /// </summary>
    public string CurrentLanguage
    {
        get => CurrentLanguageCode;
        set
        {
            if (CurrentLanguageCode != value && AvailableLanguages.Any(l => l.Code == value))
            {
                Log.Info($"Language change: {CurrentLanguageCode} → {value}");
                CurrentLanguageCode = value;
                LoadLanguage(value);

                // Update bootstrap for fast startup
                BootstrapSettings.Current.LanguageCode = value;
                BootstrapSettings.Current.Save();

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                LanguageChanged?.Invoke(this, value);
            }
        }
    }

    private LocalizationService()
    {
        Log.Info("LocalizationService created (deferred)");
    }

    /// <summary>
    /// Initializes the localization service with the specified language code.
    /// </summary>
    /// <param name="langCode">Language code (e.g., "en" or "ru").</param>
    public void Initialize(string? langCode)
    {
        // Do not block re-initialization if a previous attempt (e.g., during early startup) resulted in an empty dictionary
        if (_isInitialized && _resources.Count > 0)
        {
            Log.Warn("LocalizationService already initialized");
            UpdateApplicationResources();
            return;
        }

        var langToUse = langCode ?? "en";

        if (!AvailableLanguages.Any(l => l.Code == langToUse))
        {
            Log.Warn($"Unknown language '{langToUse}', falling back to 'en'");
            langToUse = "en";
        }

        CurrentLanguageCode = langToUse;
        LoadLanguage(langToUse);
        _isInitialized = _resources.Count > 0;

        UpdateApplicationResources();

        Log.Info($"LocalizationService initialized: {langToUse} (Keys: {_resources.Count})");
    }

    /// <summary>
    /// Loads the localization file for the specified language into memory.
    /// </summary>
    private void LoadLanguage(string langCode)
    {
        try
        {
            using var stream = OpenLocalizationStream(langCode);
            if (stream == null)
            {
                Log.Error($"Localization stream not found for '{langCode}'");
                if (langCode != "en") LoadLanguage("en");
                return;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var rawDictionary = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString)
                ?? throw new InvalidOperationException("Deserialization returned null");

            // Replace mutable Dictionary with lightweight, optimized FrozenDictionary
            _resources = rawDictionary.ToFrozenDictionary(StringComparer.Ordinal);
            _missingMarkerCache.Clear();

            // Synchronize global string resources for DynamicResource in styles and context menus (AOT)
            UpdateApplicationResources();

            Log.Info($"✓ Loaded {langCode}.json ({_resources.Count} keys into FrozenDictionary)");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load '{langCode}': {ex.Message}");

            if (langCode == "en")
            {
                _resources = FrozenDictionary<string, string>.Empty;
                Log.Warn("Using empty dictionary");
            }
            else
            {
                LoadLanguage("en");
            }
        }
    }

    /// <summary>
    /// Exports menu and base UI string keys to Application resources to support DynamicResource.
    /// Runs synchronously on the UI thread, ensuring key availability during initial style rendering.
    /// </summary>
    public void UpdateApplicationResources()
    {
        if (Avalonia.Application.Current is not { } app) return;

        void Apply()
        {
            app.Resources["L10n.ContextMenu_Cut"] = Instance["ContextMenu_Cut"];
            app.Resources["L10n.ContextMenu_Copy"] = Instance["ContextMenu_Copy"];
            app.Resources["L10n.ContextMenu_Paste"] = Instance["ContextMenu_Paste"];
            app.Resources["L10n.ContextMenu_SelectAll"] = Instance["ContextMenu_SelectAll"];
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Avalonia.Threading.Dispatcher.UIThread.Invoke(Apply);
    }

    /// <summary>
    /// Safely opens a stream to the localization file.
    /// Works via Avalonia AssetLoader (if framework is loaded) or directly via the file system during early startup.
    /// </summary>
    private static Stream? OpenLocalizationStream(string langCode)
    {
        var relativePath = Path.Combine("Assets", "Localization", $"{langCode}.json");

        // Attempt via standard Avalonia AssetLoader
        try
        {
            var uri = new Uri($"avares://LMP/Assets/Localization/{langCode}.json");
            if (AssetLoader.Exists(uri))
                return AssetLoader.Open(uri);
        }
        catch
        {
            // Avalonia is not yet configured in Main — falling back to file fallback
        }

        // Fallback: direct file search adjacent to the application executable
        var appDir = AppContext.BaseDirectory;
        var filePath = Path.Combine(appDir, relativePath);
        if (File.Exists(filePath))
            return File.OpenRead(filePath);

        // Fallback: search up the directory tree (for dotnet run / IDE debug mode)
        var dir = new DirectoryInfo(appDir);
        for (int i = 0; i < 4 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return File.OpenRead(candidate);

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Gets a localized string by key. Guarantees O(1) access and zero-allocation on cached misses.
    /// </summary>
    public string this[string key]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_resources.TryGetValue(key, out var value))
                return value;

            return ResolveMissingKey(key);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string ResolveMissingKey(string key)
    {
#if DEBUG
        if (_isInitialized)
        {
            Log.Warn($"[Localization] Missing key '{key}' for language '{CurrentLanguageCode}'");
        }
#endif
        return _missingMarkerCache.GetOrAdd(key, static k => string.Concat("[", k, "]"));
    }

    /// <summary>
    /// Get() method is completely deprecated. Use the service's direct indexer: L[key] or SL[key].
    /// </summary>
    [Obsolete("Метод Get() упразднён. Используйте индексатор L[key] или SL[key]. Fallbacks запрещены.", error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Get(string key) => this[key];

    /// <summary>
    /// Fallback values are strictly prohibited to prevent silent display of incorrect language.
    /// Add missing keys directly to localization files en.json and ru.json.
    /// </summary>
    [Obsolete("Fallback values are strictly forbidden to ensure missing localization keys are immediately visible.", error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Get(string key, string? fallback) =>
        throw new NotSupportedException("Fallbacks are disabled. Add the key to JSON dictionaries.");

    /// <summary>
    /// Returns a pluralized string. Supports CLDR categories (_one, _few, _many) and numeric suffixes (_1, _2, _5).
    /// </summary>
    public string GetPlural(string key, int count)
    {
        if (!_isInitialized) return $"{count}";

        var absCount = Math.Abs(count);
        var lastTwo = absCount % 100;
        var lastOne = absCount % 10;

        string numSuffix;
        string nameSuffix;

        if (count == 0)
        {
            numSuffix = "_0";
            nameSuffix = "_zero";
        }
        else if (lastTwo >= 11 && lastTwo <= 19)
        {
            numSuffix = "_5";
            nameSuffix = "_many";
        }
        else if (lastOne == 1)
        {
            numSuffix = "_1";
            nameSuffix = "_one";
        }
        else if (lastOne >= 2 && lastOne <= 4)
        {
            numSuffix = "_2";
            nameSuffix = "_few";
        }
        else
        {
            numSuffix = "_5";
            nameSuffix = "_many";
        }

        string? pattern = null;

        if (!_resources.TryGetValue(string.Concat(key, numSuffix), out pattern) &&
            !_resources.TryGetValue(string.Concat(key, nameSuffix), out pattern))
        {
            if (count == 0 && _resources.TryGetValue(string.Concat(key, "_many"), out var zeroMany))
            {
                pattern = zeroMany;
            }
            else if (!_resources.TryGetValue(string.Concat(key, "_other"), out pattern))
            {
                _resources.TryGetValue(key, out pattern);
            }
        }

        if (pattern != null)
        {
            if (pattern.Contains("{0}"))
                return string.Format(pattern, count);

            if (count == 0 && (numSuffix == "_0" || nameSuffix == "_zero"))
                return pattern;

            return $"{count} {pattern}";
        }

#if DEBUG
        Log.Warn($"[Localization] Missing plural keys for base '{key}' in language '{CurrentLanguageCode}'");
#endif
        return ResolveMissingKey(key);
    }
}

/// <summary>
/// Represents a supported language item in a dropdown list.
/// </summary>
public sealed class LanguageItem
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public override string ToString() => Name;
}