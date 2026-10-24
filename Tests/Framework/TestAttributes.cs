using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LMP.Tests.Framework;

/// <summary>
/// Помечает статический метод как тест или отладочное действие для автоматического обнаружения.
/// <para>
/// Поддерживаемые сигнатуры:
/// <list type="bullet">
///   <item><c>static Task MethodAsync()</c></item>
///   <item><c>static Task MethodAsync(CancellationToken ct)</c></item>
///   <item><c>static Task MethodAsync(IServiceProvider services)</c></item>
///   <item><c>static Task MethodAsync(IServiceProvider services, CancellationToken ct)</c></item>
///   <item><c>static Task MethodAsync(CancellationToken ct, IServiceProvider services)</c></item>
/// </list>
/// </para>
/// </summary>
/// <param name="category">Категория выполнения (<see cref="TestCategory"/>).</param>
/// <param name="displayName">Понятное имя для UI (если null — генерируется из имени метода).</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestMethodAttribute(
    TestCategory category,
    string? displayName = null) : Attribute
{
    /// <summary>Категория нагрузки и степень изоляции теста.</summary>
    public TestCategory Category { get; } = category;

    /// <summary>Отображаемое имя теста без дублирующих префиксов группы.</summary>
    public string? DisplayName { get; } = displayName;

    /// <summary>
    /// Тематическая подсистема из <see cref="TestGroups"/>.
    /// Если не задана, выводится автоматически из имени содержащего класса.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>Порядок сортировки внутри группы (меньше — выше в списке).</summary>
    public int Order { get; init; } = 100;

    /// <summary>Флаг обязательного наличия интернет-соединения.</summary>
    public bool RequiresNetwork { get; init; }

    /// <summary>Лимит времени выполнения в секундах (0 = без ограничения).</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Категории выполнения тестов и отладочных инструментов.</summary>
public enum TestCategory : byte
{
    /// <summary>Автономные детерминированные тесты логики без сети и внешних зависимостей.</summary>
    Unit = 0,

    /// <summary>Интеграционные тесты подсистем (сеть, дешифрация, SQLite, компоненты).</summary>
    Integration = 1,

    /// <summary>Бенчмарки производительности, пропускной способности и аллокаций памяти.</summary>
    Benchmark = 2,

    /// <summary>Сквозные сценарии полного цикла воспроизведения.</summary>
    E2E = 3,

    /// <summary>Диагностические сетевые и системные зонды (CDN ping, DNS, аудит ТСПУ).</summary>
    Diagnostic = 4,

    /// <summary>Интерактивные и деструктивные отладочные действия (генерация данных, сброс кэшей).</summary>
    Action = 5
}

/// <summary>
/// Канонические тематические группы тестов и подсистем.
/// </summary>
public static class TestGroups
{
    /// <summary>Аудио-движок, декодеры, DSP, эквалайзер, EBU R128 pre-scan и zero-alloc буферы.</summary>
    public const string Audio = "Audio";

    /// <summary>Дешифрация YouTube: NToken, SigCipher, PoToken (BotGuard), AST Solver и QuickJS.</summary>
    public const string Cipher = "Cipher";

    /// <summary>Сетевой уровень: CDN, DNS/DoH, диагностика ТСПУ/DPI, время жизни URL.</summary>
    public const string Network = "Network";

    /// <summary>Парсеры YouTube/YTM (Search, PlayerResponse, Browse) и конвейер стриминга.</summary>
    public const string Youtube = "Youtube";

    /// <summary>Локальное хранилище: SQLite, MemoryPack, AtomicFile, дисковые и RAM кэши.</summary>
    public const string Storage = "Storage";

    /// <summary>Плейлисты: порядок следования треков, синхронизация, CRUD и миграции.</summary>
    public const string Playlists = "Playlists";

    /// <summary>Системные службы: автообновление, уведомления, мониторинг UI.</summary>
    public const string System = "System";

    // Алиасы для плавной обратной совместимости
    public const string NToken = Cipher;
    public const string SigCipher = Cipher;
    public const string Solver = Cipher;
    public const string Pipeline = Youtube;
    public const string Cache = Storage;
    public const string Notifications = System;

    /// <summary>
    /// Нормализует произвольную строку группы к канонической константе <see cref="TestGroups"/>.
    /// </summary>
    public static string Normalize(string? rawGroup) => rawGroup switch
    {
        null or "" => System,
        "NToken" or "SigCipher" or "Solver" or "PoToken" or Cipher => Cipher,
        "CdnDiagnostic" or "StreamFreshness" or Network => Network,
        "Pipeline" or "StreamPipeline" or "Search" or "Player" or Youtube => Youtube,
        "Cache" or "AtomicFile" or "MemoryPack" or Storage => Storage,
        "Audio" or "PreScan" or "HotPath" => Audio,
        "Playlist" or "PlaylistOrder" or Playlists => Playlists,
        "Update" or "Notifications" or "Notification" or System => System,
        _ => rawGroup
    };
}

/// <summary>
/// Исключение, выбрасываемое при невозможности выполнения теста из-за невыполненных предусловий.
/// Переводит тест в статус <see cref="TestRunState.Skipped"/> вместо ложного прохождения.
/// </summary>
public sealed class TestSkippedException(string reason) : Exception(reason)
{
    /// <summary>Причина пропуска теста для отображения в UI.</summary>
    public string Reason => Message;
}

/// <summary>
/// Централизованный легковесный API утверждений (Assertions).
/// Захватывает проверяемое выражение через <see cref="CallerArgumentExpressionAttribute"/>.
/// </summary>
public static class TestAssert
{
    /// <summary>Проверяет истинность условия.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void True(
        [DoesNotReturnIf(false)] bool condition,
        string? message = null,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition)
            ThrowFailure(message ?? $"Expected condition to be true: {expr}");
    }

    /// <summary>Проверяет ложность условия.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void False(
        [DoesNotReturnIf(true)] bool condition,
        string? message = null,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition)
            ThrowFailure(message ?? $"Expected condition to be false: {expr}");
    }

    /// <summary>Проверяет равенство двух значений.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Equal<T>(
        T expected,
        T actual,
        string? message = null,
        [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            ThrowFailure(message ?? $"Equal assertion failed for '{expr}': expected '{expected}', got '{actual}'");
    }

    /// <summary>Проверяет, что ссылка не равна null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NotNull<T>(
        [NotNull] T? value,
        string? message = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null) where T : class
    {
        if (value is null)
            ThrowFailure(message ?? $"Expected '{expr}' to be non-null");
        return value;
    }

    /// <summary>Проверяет, что строка не пуста и не равна null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string NotNullOrEmpty(
        [NotNull] string? value,
        string? message = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null)
    {
        if (string.IsNullOrEmpty(value))
            ThrowFailure(message ?? $"Expected '{expr}' to be non-empty string");
        return value;
    }

    /// <summary>Прерывает выполнение теста и переводит его в состояние Skipped.</summary>
    [DoesNotReturn]
    public static void Skip(string reason) =>
        throw new TestSkippedException(reason);

    /// <summary>Прерывает выполнение теста с сообщением об ошибке.</summary>
    [DoesNotReturn]
    public static void Fail(string message) =>
        throw new InvalidOperationException($"[TestAssert.Fail] {message}");

    /// <summary>
    /// Проверяет отсутствие управляемых аллокаций в горячем цикле.
    /// </summary>
    public static void ZeroAlloc(Action action, int warmup = 5, int iterations = 1000, string? context = null)
    {
        for (int i = 0; i < warmup; i++)
            action();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            action();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
        {
            ThrowFailure(
                $"{context ?? "ZeroAlloc"}: expected 0 B allocated, got {allocated} B over {iterations} calls ({(double)allocated / iterations:F2} B/call)");
        }
    }

    [DoesNotReturn]
    private static void ThrowFailure(string message) =>
        throw new InvalidOperationException($"[Assert] {message}");
}