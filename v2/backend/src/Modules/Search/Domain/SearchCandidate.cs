namespace PersonalDashboard.V2.Search.Domain;

public sealed record IndexedChatContext(Guid ConversationId, Guid TurnId, string Mode,
    string? EntityType, Guid? EntityId, long? EntityVersion);

public sealed record IndexedSource(
    string Kind, Guid Id, long Version, string Title, string? Path, string? Url, DateTimeOffset UpdatedAtUtc,
    IndexedChatContext? ChatContext = null)
{
    public string? EntityType => ChatContext?.EntityType;
    public Guid? EntityId => ChatContext?.EntityId;
    public long? EntityVersion => ChatContext?.EntityVersion;
}

/// <summary>One indexed chunk with scores returned by PostgreSQL full text and vector retrieval.</summary>
public sealed record SearchCandidate(
    IndexedSource Source, int ChunkIndex, string Text,
    double? SemanticScore = null, double? FullTextScore = null);

public sealed record SearchContextFilter(Guid? ConversationId = null, string? Mode = null,
    string? EntityType = null, Guid? EntityId = null, long? EntityVersion = null);

public sealed record SearchCriteria(
    string Query, bool Exhaustive = false, IReadOnlyList<string>? Kinds = null, SearchContextFilter? Context = null,
    DateTimeOffset? UpdatedAfterUtc = null, DateTimeOffset? UpdatedBeforeUtc = null);

public sealed record RankedSource(
    string Kind, Guid Id, long Version, string? Url, string Title, string? Path, DateTimeOffset UpdatedAtUtc,
    IndexedChatContext? ChatContext, string Snippet, bool IsChatHistory, bool IsSemantic, double Score);
