using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Models;

/// <summary>
/// Generic collection of items returned by a single request.
/// </summary>
public class Batch<T>(IReadOnlyList<T> items, string? continuationToken = null)
    where T : IBatchItem
{
    /// <summary>
    /// Items included in the batch.
    /// </summary>
    public IReadOnlyList<T> Items { get; } = items;

    /// <summary>
    /// Токен продолжения для последующей страницы выборки InnerTube.
    /// </summary>
    public string? ContinuationToken { get; } = continuationToken;
}

internal static class Batch
{
    public static Batch<T> Create<T>(IReadOnlyList<T> items, string? continuationToken = null)
        where T : IBatchItem => new(items, continuationToken);
}

internal static class BatchExtensions
{
    extension<T>(IAsyncEnumerable<Batch<T>> source)
        where T : IBatchItem
    {
        public IAsyncEnumerable<T> FlattenAsync() => source.SelectManyAsync(static b => b.Items);
    }
}