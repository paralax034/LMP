using System.Reflection;

namespace LMP.Tests.Framework;

/// <summary>
/// Сигнатура параметров тестового метода для эффективного вызова без рефлексивного перебора.
/// </summary>
public enum TestParameterSignature : byte
{
    None = 0,
    ServicesOnly = 1,
    TokenOnly = 2,
    ServicesAndToken = 3,
    TokenAndServices = 4
}

/// <summary>
/// Дескриптор зарегистрированного теста или отладочного действия.
/// </summary>
public sealed record TestDescriptor
{
    /// <summary>Уникальный идентификатор: "ClassName.MethodName".</summary>
    public required string Id { get; init; }

    /// <summary>Человекочитаемое имя для UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Категория выполнения теста.</summary>
    public required TestCategory Category { get; init; }

    /// <summary>Тематическая подсистема из <see cref="TestGroups"/>.</summary>
    public required string Group { get; init; }

    /// <summary>Имя содержащего класса.</summary>
    public required string ClassName { get; init; }

    /// <summary>Порядок сортировки.</summary>
    public required int Order { get; init; }

    /// <summary>Требует ли тест активной сети.</summary>
    public required bool RequiresNetwork { get; init; }

    /// <summary>Таймаут в секундах (0 = без ограничения).</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>Является ли элемент интерактивным отладочным действием.</summary>
    public bool IsAction => Category == TestCategory.Action;

    /// <summary>Является ли элемент диагностическим зондом.</summary>
    public bool IsDiagnostic => Category == TestCategory.Diagnostic;

    /// <summary>Сигнатура параметров метода.</summary>
    public required TestParameterSignature Signature { get; init; }

    /// <summary>Отражение метода теста для прямого вызова.</summary>
    public required MethodInfo Method { get; init; }
}

/// <summary>Состояние выполнения теста.</summary>
public enum TestRunState : byte
{
    NotRun = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
    Skipped = 4
}

/// <summary>
/// Результат выполнения теста или действия.
/// </summary>
public sealed record TestResult
{
    /// <summary>Идентификатор теста.</summary>
    public required string TestId { get; init; }

    /// <summary>Итоговый статус выполнения.</summary>
    public required TestRunState State { get; init; }

    /// <summary>Затраченное время.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Текст ошибки или причина пропуска (null при успешном прохождении).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Форматированное время для отображения в UI.</summary>
    public string DurationFormatted => Duration.TotalMilliseconds < 1000
        ? $"{Duration.TotalMilliseconds:F0}ms"
        : $"{Duration.TotalSeconds:F1}s";
}