using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Application;

/// <summary>Module-owned persistence port. Implementations must commit each callback atomically.</summary>
public interface IKnowledgeStore
{
    Task<TResult> InTransactionAsync<TResult>(Func<IKnowledgeTransaction, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);
}

public interface IKnowledgeSearchPublisher
{
    Task PublishAsync(KnowledgeMutation mutation, CancellationToken cancellationToken);
}

public interface IKnowledgeTransaction
{
    Task<IReadOnlyList<KnowledgeNode>> GetLiveNodesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<KnowledgeNode>> GetAllNodesAsync(CancellationToken cancellationToken);
    Task AddAsync(KnowledgeNode node, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record KnowledgeNodeView(
    Guid Id, string Kind, string Title, string Markdown, Guid? ParentId,
    int Position, long Version, bool Deleted, bool Archived, DateTimeOffset UpdatedAt, string Path);

public sealed record KnowledgeMutation(IReadOnlyList<KnowledgeNodeView> ChangedNodes);

public sealed record KnowledgeSearchResult(Guid Id, string Title, Guid? ParentId, string Path, string Snippet, long Version, DateTimeOffset UpdatedAt);

public static class KnowledgeViews
{
    public static KnowledgeNodeView ToView(this KnowledgeNode node, IReadOnlyCollection<KnowledgeNode> all) =>
        new(node.Id, node.Type == KnowledgeNodeType.Section ? "section" : "document", node.Title, node.Markdown,
            node.ParentId, node.Position, node.Version, node.IsDeleted, node.IsArchived, node.UpdatedAt,
            KnowledgeTree.GetPath(all, node));
}
