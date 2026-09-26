namespace LMP.Tests.Framework;

/// <summary>
/// Помечает статический async-метод как тест для автоматического обнаружения.
/// <para>
/// Метод должен иметь сигнатуру:
/// <c>static Task MethodAsync()</c> — для unit-тестов, или
/// <c>static Task MethodAsync(IServiceProvider)</c> — для integration-тестов.
/// </para>
/// </summary>
/// <param name="category">Категория теста (Unit, Integration, Benchmark).</param>
/// <param name="displayName">Имя для отображения в UI. Если null — берётся имя метода.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestMethodAttribute(
    TestCategory category,
    string? displayName = null) : Attribute
{
    /// <summary>Категория теста для группировки в UI.</summary>
    public TestCategory Category { get; } = category;

    /// <summary>
    /// Человекочитаемое имя теста.
    /// Если не задано, генерируется из имени метода.
    /// </summary>
    public string? DisplayName { get; } = displayName;

    /// <summary>
    /// Тематическая группа для фильтрации: "NToken", "SigCipher", "Pipeline", "Cache", "Solver".
    /// <para>
    /// Позволяет быстро отфильтровать все тесты по одной подсистеме,
    /// независимо от категории (Unit/Integration/Benchmark).
    /// </para>
    /// Если не задано — берётся из имени класса (убирается суффикс "Tests").
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Порядок сортировки внутри категории. Меньше — выше в списке.
    /// </summary>
    public int Order { get; init; } = 100;

    /// <summary>
    /// Требуется ли сеть для выполнения теста.
    /// </summary>
    public bool RequiresNetwork { get; init; }

    /// <summary>
    /// Максимальное время выполнения в секундах. 0 = без ограничения.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Категории тестов для группировки и фильтрации.</summary>
public enum TestCategory
{
    /// <summary>Unit-тесты: без сети, без DI, детерминированные.</summary>
    Unit,

    /// <summary>Integration-тесты: требуют сеть и/или DI-контейнер.</summary>
    Integration,

    /// <summary>Benchmarks: измеряют производительность.</summary>
    Benchmark,

    E2E
}

/// <summary>
/// Известные тематические группы тестов.
/// Используются как константы для <see cref="TestMethodAttribute.Group"/>.
/// </summary>
public static class TestGroups
{
    public const string NToken = "NToken";
    public const string SigCipher = "SigCipher";
    public const string Solver = "Solver";
    public const string Pipeline = "Pipeline";
    public const string Cache = "Cache";
    public const string Audio = "Audio";
    public const string Notifications = "Notifications";
}