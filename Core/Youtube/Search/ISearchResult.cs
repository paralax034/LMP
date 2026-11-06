namespace LMP.Core.Youtube.Search;

/// <summary>
/// Common interface for search results (Videos, Playlists, Channels)
/// </summary>
public interface ISearchResult : IBatchItem
{
    string Url { get; }
    string Title { get; }
}