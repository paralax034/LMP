using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Platform;

namespace LMP.Core.Services;

/// <summary>
/// Сервис локализации и многоязыковой поддержки приложения.
/// <para>
/// Оптимизирован для AOT и zero-allocation в горячих путях за счет использования <see cref="FrozenDictionary{TKey, TValue}"/>
/// и пула кэширования отсутствующих ключей.
/// </para>
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static readonly LocalizationService Instance = new();

    /// <summary>
    /// Неизменяемый FrozenDictionary для O(1) поиска ключей без накладных расходов синхронизации и проверок коллизий.
    /// </summary>
    private FrozenDictionary<string, string> _resources = FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// Пул отсутствующих маркеров ключей вида [key] для предотвращения GC-аллокаций при повторных запросах.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _missingMarkerCache = new(StringComparer.Ordinal);

    private bool _isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    /// <summary>
    /// Возвращает список поддерживаемых языков интерфейса.
    /// </summary>
    public List<LanguageItem> AvailableLanguages { get; } =
    [
        new() { Code = "en", Name = "English" },
        new() { Code = "ru", Name = "Русский" }
    ];

    /// <summary>
    /// Возвращает код текущего языка интерфейса.
    /// </summary>
    public string CurrentLanguageCode { get; private set; } = "en";

    /// <summary>
    /// Получает или задает текущий язык приложения. При изменении триггерит перезагрузку ресурсов и событие смены языка.
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

                // Обновить bootstrap для быстрого старта
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
    /// Инициализирует службу локализации выбранным кодом языка.
    /// </summary>
    /// <param name="langCode">Код языка (например, "en" или "ru").</param>
    public void Initialize(string? langCode)
    {
        // Не блокируем повторную инициализацию, если предыдущая попытка (например, на раннем старте) завершилась с пустым словарем
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
    /// Загружает файл локализации для указанного языка в память.
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

            // Замена изменяемого Dictionary на легковесный, оптимизированный FrozenDictionary
            _resources = rawDictionary.ToFrozenDictionary(StringComparer.Ordinal);
            _missingMarkerCache.Clear();

            // Синхронизация глобальных строковых ресурсов для DynamicResource в стилях и контекстных меню (AOT)
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
    /// Экспортирует строковые ключи меню и базового интерфейса в ресурсы Application для поддержки DynamicResource.
    /// Выполняется синхронно на UI-потоке, гарантируя доступность ключей при первичной отрисовке стилей.
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
    /// Безопасно открывает поток файла локализации.
    /// Работает как через Avalonia AssetLoader (если фреймворк загружен), так и напрямую через файловую систему на этапе раннего запуска.
    /// </summary>
    private static Stream? OpenLocalizationStream(string langCode)
    {
        var relativePath = Path.Combine("Assets", "Localization", $"{langCode}.json");

        // Попытка через стандартный AssetLoader Avalonia
        try
        {
            var uri = new Uri($"avares://LMP/Assets/Localization/{langCode}.json");
            if (AssetLoader.Exists(uri))
                return AssetLoader.Open(uri);
        }
        catch
        {
            // Avalonia ещё не сконфигурирована в Main — переходим к файловому fallback
        }

        // Fallback: прямой поиск файла рядом с исполняемым файлом приложения
        var appDir = AppContext.BaseDirectory;
        var filePath = Path.Combine(appDir, relativePath);
        if (File.Exists(filePath))
            return File.OpenRead(filePath);

        // Fallback: поиск вверх по дереву папок (для режима отладки dotnet run / IDE)
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
    /// Получает локализованную строку по ключу. Гарантирует O(1) доступ и zero-allocation на кэшированных промахах.
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
    /// Метод Get() полностью упразднён. Используйте прямой индексатор сервиса: L[key] или SL[key].
    /// </summary>
    [Obsolete("Метод Get() упразднён. Используйте индексатор L[key] или SL[key]. Fallbacks запрещены.", error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Get(string key) => this[key];

    /// <summary>
    /// Использование fallback-значений строго запрещено для исключения скрытого отображения неверного языка.
    /// Добавляйте недостающие ключи напрямую в файлы локализации en.json и ru.json.
    /// </summary>
    [Obsolete("Fallback values are strictly forbidden to ensure missing localization keys are immediately visible.", error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Get(string key, string? fallback) =>
        throw new NotSupportedException("Fallbacks are disabled. Add the key to JSON dictionaries.");

    /// <summary>
    /// Возвращает плюрализованную строку. Поддерживает CLDR категории (_one, _few, _many) и числовые суффиксы (_1, _2, _5).
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
/// Представляет элемент поддерживаемого языка в выпадающем списке.
/// </summary>
public sealed class LanguageItem
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public override string ToString() => Name;
}