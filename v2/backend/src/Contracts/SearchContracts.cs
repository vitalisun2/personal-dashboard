namespace PersonalDashboard.V2.Contracts.Search;

public sealed record SearchChatContext(
    Guid ConversationId,
    Guid TurnId,
    string Mode,
    string? EntityType,
    Guid? EntityId,
    long? EntityVersion);

public sealed record SearchIndexSource(
    string Kind,
    Guid Id,
    long Version,
    string Title,
    string Body,
    string? Path,
    string? Url,
    DateTimeOffset UpdatedAtUtc,
    SearchChatContext? ChatContext = null);

public sealed record SearchSourceChange(string Kind, Guid Id, long Version, bool Deleted, SearchIndexSource? Source);

public sealed record SearchSourcePage(IReadOnlyList<SearchSourceChange> Changes, string? NextCursor, bool IsComplete);

public interface ISearchIndexer
{
    Task UpsertAsync(SearchIndexSource source, CancellationToken cancellationToken = default);

    Task DeleteAsync(string kind, Guid id, long version, CancellationToken cancellationToken = default);
}

public interface ISearchSourceFeed
{
    Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default);
}

public enum SearchCoverageMode
{
    Relevant,
    Exhaustive
}

public sealed record SearchRequest(
    string Query,
    SearchCoverageMode Mode = SearchCoverageMode.Relevant,
    IReadOnlyList<string>? Kinds = null,
    SearchChatContext? Context = null,
    DateTimeOffset? UpdatedAfterUtc = null,
    DateTimeOffset? UpdatedBeforeUtc = null,
    string? Cursor = null,
    int? PageSize = null);

public sealed record SearchSourceReference(string Kind, Guid Id, long Version, string? Url, string Snippet, bool IsChatHistory);

public sealed record SearchHit(SearchSourceReference Source, double Score);

public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Hits,
    string? NextCursor,
    bool IsComplete,
    string? CoverageNote);

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}
