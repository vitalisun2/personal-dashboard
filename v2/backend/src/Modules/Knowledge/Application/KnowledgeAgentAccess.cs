using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Application;

/// <summary>Typed, version-checked Knowledge operations for Agent proposals.</summary>
public sealed class KnowledgeAgentAccess(KnowledgeService knowledge) : IKnowledgeAgentAccess
{
    public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) =>
        knowledge.GetAgentStateAsync(kind, id, cancellationToken);

    public async Task<KnowledgeMutationResult> ApplyAsync(PersonalDashboard.V2.Contracts.AgentAccess.KnowledgeMutation mutation, CancellationToken cancellationToken = default)
    {
        var current = await knowledge.GetAgentStateAsync(mutation.Kind, mutation.Id, cancellationToken);
        if (mutation.Operation is not (KnowledgeMutationKind.Create or KnowledgeMutationKind.Reorder) && current is null)
            return new KnowledgeMutationResult(false, null, "Knowledge node was not found or its kind does not match.");
        if (mutation.Operation == KnowledgeMutationKind.Create && current is not null)
            return new KnowledgeMutationResult(false, current, "Knowledge node ID already exists.");

        try
        {
            var changed = await knowledge.ApplyAgentMutationAsync(mutation, cancellationToken);
            var node = changed.ChangedNodes.FirstOrDefault(n => n.Id == mutation.Id);
            if (node is null) return new KnowledgeMutationResult(true, await knowledge.GetAgentStateAsync(mutation.Kind, mutation.Id, cancellationToken), null);
            if (node.Deleted) return new KnowledgeMutationResult(true, null, null);
            var state = ToState(node);
            return new KnowledgeMutationResult(true, state, null);
        }
        catch (KnowledgeVersionConflictException ex)
        {
            return new KnowledgeMutationResult(false, await knowledge.GetAgentStateAsync(mutation.Kind, mutation.Id, cancellationToken), ex.Message);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            return new KnowledgeMutationResult(false, await knowledge.GetAgentStateAsync(mutation.Kind, mutation.Id, cancellationToken), ex.Message);
        }
    }

    private static KnowledgeNodeState ToState(KnowledgeNodeView node) => new(
        node.Kind == "section" ? KnowledgeNodeKind.Section : KnowledgeNodeKind.Document,
        node.Id, node.ParentId, node.Version, node.Title, node.Kind == "document" ? node.Markdown : null,
        node.Path, node.Archived, node.Position);
}
