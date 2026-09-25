namespace PersonalDashboard.V2.Contracts.Knowledge;

public sealed record KnowledgeDocumentState(
    Guid Id,
    Guid? ParentSectionId,
    string Title,
    string Markdown,
    string Path,
    long Version,
    DateTimeOffset UpdatedAtUtc);

// A null field retains its current value; an empty Markdown string clears it.
public sealed record KnowledgeDocumentEdit(Guid Id, long ExpectedVersion, string? Title, string? Markdown);

public sealed record KnowledgeDocumentEditResult(
    bool Applied,
    KnowledgeDocumentState? Current,
    string? ConflictReason);

public interface IKnowledgeDocumentAccess
{
    Task<KnowledgeDocumentState?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<KnowledgeDocumentEditResult> EditAsync(
        KnowledgeDocumentEdit edit,
        CancellationToken cancellationToken = default);
}
