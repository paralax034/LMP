using System.Runtime.CompilerServices;
using LMP.Core.Audio.Sources;

namespace LMP.Tests.Unit;

/// <summary>
/// Типизированный доступ тестов к закрытым членам <see cref="CachingStreamSource"/> без рефлексии (совместим с AOT).
/// Сигнатуры связываются при первом вызове: переименование или смена типов в источнике
/// даёт <see cref="MissingMethodException"/> или <see cref="MissingFieldException"/>, а не молчаливый пропуск проверки.
/// </summary>
internal static class CachingStreamSourceAccessors
{
    /// <summary>Координированный refresh stream URL: single-flight и единый бюджет HTTP 403.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CoordinatedRefreshAsync")]
    internal static extern Task<CachingStreamSource.RefreshOutcome> CoordinatedRefreshAsync(
        CachingStreamSource source, CancellationToken ct);

    /// <summary>Ожидание refresh, уже начатого другим потоком.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WaitForConcurrentRefreshAsync")]
    internal static extern Task<CachingStreamSource.RefreshOutcome> WaitForConcurrentRefreshAsync(
        CachingStreamSource source, CancellationToken ct);

    /// <summary>Single-flight получение URL для источника без адреса.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EnsureUrlAvailableAsync")]
    internal static extern Task<bool> EnsureUrlAvailableAsync(CachingStreamSource source, CancellationToken ct);

    /// <summary>Диагностика ответа HTTP 403 с немедленной инвалидацией SessionCacheStore.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "LogAndDiagnose403Async")]
    internal static extern Task LogAndDiagnose403Async(
        CachingStreamSource source, int logicalIndex, HttpRequestMessage request, HttpResponseMessage response);

    /// <summary>Таблица решений refresh; первый параметр задаёт тип-владелец статического метода и всегда <c>null</c>.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "DecideRefresh")]
    internal static extern CachingStreamSource.RefreshDecision DecideRefresh(
        CachingStreamSource? owner, CachingStreamSource.RefreshOutcome outcome, bool forbiddenDriven);

    /// <summary>Счётчик подряд полученных HTTP 403, от которого зависит списание бюджета.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_consecutive403Count")]
    internal static extern ref int Consecutive403Count(CachingStreamSource source);
}