using System.Text.Json;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Knowledge;
using PersonalDashboard.V2.Knowledge.Domain;
using AgentMutation = PersonalDashboard.V2.Contracts.AgentAccess.KnowledgeMutation;

namespace PersonalDashboard.V2.Knowledge.Application;

/// <summary>Version-checked Knowledge commands and tree queries.</summary>
public sealed partial class KnowledgeService(IKnowledgeStore store, IEntityChangeJournal changeJournal, IKnowledgeSearchPublisher searchPublisher, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<KnowledgeNodeView>> GetTreeAsync(CancellationToken cancellationToken = default) =>
        store.InTransactionAsync(async (tx, ct) =>
        {
            var nodes = await tx.GetLiveNodesAsync(ct);
            return (IReadOnlyList<KnowledgeNodeView>)nodes.OrderBy(n => n.ParentId).ThenBy(n => n.Position)
                .Select(n => n.ToView(nodes)).ToList();
        }, cancellationToken);

    public Task<KnowledgeMutation> CreateAsync(KnowledgeNodeType type, string title, Guid? parentId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async (tx, ct) =>
        {
            var nodes = await tx.GetLiveNodesAsync(ct);
            KnowledgeTree.EnsureValidParent(nodes, parentId);
            var node = KnowledgeNode.Create(type, title, parentId, nodes.Count(n => n.ParentId == parentId), timeProvider.GetUtcNow());
            await tx.AddAsync(node, ct);
            await tx.SaveChangesAsync(ct);
            var mutation = MakeMutation([node], nodes.Append(node).ToArray());
            await AppendChangesAsync(mutation, ct);
            return mutation;
        }, cancellationToken);

    public Task<KnowledgeMutation> CreateWithIdAsync(Guid id, KnowledgeNodeType type, string title, string markdown, Guid? parentId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async (tx, ct) =>
        {
            var all = await tx.GetAllNodesAsync(ct);
            KnowledgeTree.EnsureValidParent(all, parentId);
            if (all.Any(n => n.Id == id)) throw new InvalidOperationException("A Knowledge node with this ID already exists.");
            var node = KnowledgeNode.Create(type, title, parentId, all.Count(n => n.IsActive && n.ParentId == parentId), timeProvider.GetUtcNow(), id, markdown);
            await tx.AddAsync(node, ct);
            await tx.SaveChangesAsync(ct);
            var mutation = MakeMutation([node], all.Append(node).ToArray());
            await AppendChangesAsync(mutation, ct);
            return mutation;
        }, cancellationToken);

    public Task<KnowledgeMutation> UpdateDocumentAsync(Guid id, string title, string markdown, long expectedVersion, CancellationToken cancellationToken = default) =>
        EditDocumentAsync(id, title, markdown, expectedVersion, cancellationToken);

    public Task<KnowledgeMutation> EditDocumentAsync(Guid id, string? title, string? markdown, long expectedVersion, CancellationToken cancellationToken = default) =>
        MutateAsync(async (tx, nodes, ct) =>
        {
            var node = Find(nodes, id);
            node.EditDocument(title, markdown, expectedVersion, timeProvider.GetUtcNow());
            await tx.SaveChangesAsync(ct);
            return [node];
        }, cancellationToken);

    public async Task<KnowledgeDocumentState?> GetDocumentStateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var nodes = await GetTreeAsync(cancellationToken);
        var node = nodes.FirstOrDefault(n => n.Id == id && n.Kind == "document");
        return node is null ? null : new KnowledgeDocumentState(node.Id, node.ParentId, node.Title, node.Markdown,
            node.Path, node.Version, node.UpdatedAt);
    }

    public Task<KnowledgeMutation> RenameAsync(Guid id, string title, long expectedVersion, CancellationToken cancellationToken = default) =>
        MutateAsync(async (tx, nodes, ct) =>
        {
            var node = Find(nodes, id);
            var affected = KnowledgeTree.DescendantsAndSelf(nodes, id);
            node.Rename(title, expectedVersion, timeProvider.GetUtcNow());
            await tx.SaveChangesAsync(ct);
            return affected;
        }, cancellationToken);

    public Task<KnowledgeMutation> MoveAsync(Guid id, Guid? targetId, string placement, long expectedVersion, CancellationToken cancellationToken = default) =>
        MutateAsync(async (tx, nodes, ct) =>
        {
            var node = Find(nodes, id);
            Guid? parentId;
            int index;
            if (placement == "inside")
            {
                if (targetId is not Guid target) throw new ArgumentException("Inside placement requires a target section.");
                parentId = target;
                index = nodes.Count(n => n.ParentId == target);
            }
            else
            {
                var target = targetId is Guid targetIdValue ? Find(nodes, targetIdValue) : throw new ArgumentException("Before/after placement requires a target.");
                if (target.Id == id) throw new InvalidOperationException("A node cannot be positioned relative to itself.");
                parentId = target.ParentId;
                if (placement is not ("before" or "after")) throw new ArgumentOutOfRangeException(nameof(placement));
                var remaining = nodes.Where(n => n.ParentId == parentId && n.Id != id).OrderBy(n => n.Position).ToList();
                index = remaining.FindIndex(n => n.Id == target.Id) + (placement == "after" ? 1 : 0);
            }

            KnowledgeTree.EnsureValidParent(nodes, parentId, id);
            var oldSiblings = nodes.Where(n => n.ParentId == node.ParentId && n.Id != id).OrderBy(n => n.Position).ToList();
            var newSiblings = nodes.Where(n => n.ParentId == parentId && n.Id != id).OrderBy(n => n.Position).ToList();
            index = Math.Clamp(index, 0, newSiblings.Count);
            newSiblings.Insert(index, node);
            var now = timeProvider.GetUtcNow();
            node.Move(parentId, index, expectedVersion, now);
            var changedSiblings = Compact(oldSiblings, now).Concat(Compact(newSiblings, now));
            var changed = KnowledgeTree.DescendantsAndSelf(nodes, id).Concat(changedSiblings).DistinctBy(n => n.Id).ToArray();
            await tx.SaveChangesAsync(ct);
            return changed;
        }, cancellationToken);

    public Task<KnowledgeMutation> DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async (tx, ct) =>
        {
            var all = await tx.GetAllNodesAsync(ct);
            var root = all.FirstOrDefault(n => n.Id == id && !n.IsDeleted)
                ?? throw new KeyNotFoundException($"Knowledge node {id} was not found.");
            var subtree = KnowledgeTree.NonDeletedDescendantsAndSelf(all, id);
            var siblings = all.Where(n => n.ParentId == root.ParentId && n.Id != id && n.IsActive).OrderBy(n => n.Position).ToList();
            root.Delete(expectedVersion, timeProvider.GetUtcNow());
            foreach (var child in subtree.Skip(1)) child.Delete(child.Version, timeProvider.GetUtcNow());
            var changedSiblings = Compact(siblings, timeProvider.GetUtcNow());
            await tx.SaveChangesAsync(ct);
            var changed = subtree.Concat(changedSiblings).DistinctBy(n => n.Id).ToArray();
            var mutation = MakeMutation(changed, all);
            await AppendChangesAsync(mutation, ct);
            return mutation;
        }, cancellationToken);

    public Task<KnowledgeMutation> ArchiveAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async (tx, ct) =>
        {
            var nodes = await tx.GetLiveNodesAsync(ct);
            var subtree = KnowledgeTree.DescendantsAndSelf(nodes, id);
            var root = subtree[0];
            var siblings = nodes.Where(n => n.ParentId == root.ParentId && n.Id != id).OrderBy(n => n.Position).ToList();
            var now = timeProvider.GetUtcNow();
            root.Archive(expectedVersion, now);
            foreach (var child in subtree.Skip(1)) child.Archive(child.Version, now);
            var changedSiblings = Compact(siblings, now);
            await tx.SaveChangesAsync(ct);
            var mutation = MakeMutation(subtree.Concat(changedSiblings).DistinctBy(n => n.Id), nodes);
            await AppendChangesAsync(mutation, ct);
            return mutation;
        }, cancellationToken);

    public Task<KnowledgeMutation> RestoreAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async (tx, ct) =>
        {
            var all = await tx.GetAllNodesAsync(ct);
            var subtree = KnowledgeTree.ArchivedDescendantsAndSelf(all, id);
            var root = subtree[0];
            KnowledgeTree.EnsureValidParent(all, root.ParentId);
            var siblings = all.Where(n => n.ParentId == root.ParentId && n.IsActive).OrderBy(n => n.Position).ToList();
            var position = Math.Clamp(root.Position, 0, siblings.Count);
            var now = timeProvider.GetUtcNow();
            root.Restore(expectedVersion, now);
            foreach (var child in subtree.Skip(1)) child.Restore(child.Version, now);
            siblings.Insert(position, root);
            var changedSiblings = Compact(siblings, now);
            await tx.SaveChangesAsync(ct);
            var mutation = MakeMutation(subtree.Concat(changedSiblings).DistinctBy(n => n.Id), all);
            await AppendChangesAsync(mutation, ct);
            return mutation;
        }, cancellationToken);

    public Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        store.InTransactionAsync(async (tx, ct) =>
        {
            var term = query?.Trim();
            if (string.IsNullOrEmpty(term)) return (IReadOnlyList<KnowledgeSearchResult>)[];
            var nodes = await tx.GetLiveNodesAsync(ct);
            return nodes.Where(n => n.Type == KnowledgeNodeType.Document &&
                    (n.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || n.Markdown.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
                .Select(n => new KnowledgeSearchResult(n.Id, n.Title, n.ParentId, KnowledgeTree.GetPath(nodes, n), MakeSnippet(n.Markdown, term), n.Version, n.UpdatedAt))
                .ToList();
        }, cancellationToken);

    public async Task<KnowledgeNodeState?> GetAgentStateAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default)
    {
        var nodes = await store.InTransactionAsync((tx, ct) => tx.GetAllNodesAsync(ct), cancellationToken);
        var node = nodes.FirstOrDefault(n => n.Id == id && !n.IsDeleted && AgentKind(n.Type) == kind);
        return node is null ? null : AgentState(node, nodes);
    }

    public Task<KnowledgeMutation> ApplyAgentMutationAsync(AgentMutation request, CancellationToken cancellationToken = default) => request.Operation switch
    {
        KnowledgeMutationKind.Create => CreateWithIdAsync(request.Id, DomainKind(request.Kind), RequiredTitle(request.Title), request.Markdown ?? string.Empty, request.ParentSectionId, cancellationToken),
        KnowledgeMutationKind.Update => AgentUpdateAsync(request, cancellationToken),
        KnowledgeMutationKind.Move => MoveToParentAsync(request.Id, request.ParentSectionId, RequiredVersion(request), cancellationToken),
        KnowledgeMutationKind.Archive => ArchiveAsync(request.Id, RequiredVersion(request), cancellationToken),
        KnowledgeMutationKind.Restore => RestoreAsync(request.Id, RequiredVersion(request), cancellationToken),
        KnowledgeMutationKind.Delete => DeleteAsync(request.Id, RequiredVersion(request), cancellationToken),
        KnowledgeMutationKind.Reorder => ReorderAsync(request, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(request.Operation))
    };

    private Task<KnowledgeMutation> AgentUpdateAsync(AgentMutation request, CancellationToken ct) =>
        request.Kind == KnowledgeNodeKind.Document
            ? EditDocumentAsync(request.Id, request.Title, request.Markdown, RequiredVersion(request), ct)
            : RenameAsync(request.Id, RequiredTitle(request.Title), RequiredVersion(request), ct);

    private Task<KnowledgeMutation> MoveToParentAsync(Guid id, Guid? parentId, long expectedVersion, CancellationToken ct) =>
        ExecuteMutationAsync(async (tx, token) =>
        {
            var nodes = await tx.GetLiveNodesAsync(token);
            var node = Find(nodes, id);
            KnowledgeTree.EnsureValidParent(nodes, parentId, id);
            var oldSiblings = nodes.Where(n => n.ParentId == node.ParentId && n.Id != id).OrderBy(n => n.Position).ToList();
            var newSiblings = nodes.Where(n => n.ParentId == parentId && n.Id != id).OrderBy(n => n.Position).ToList();
            var now = timeProvider.GetUtcNow();
            node.Move(parentId, newSiblings.Count, expectedVersion, now);
            newSiblings.Add(node);
            var changedSiblings = Compact(oldSiblings, now).Concat(Compact(newSiblings, now));
            var changed = KnowledgeTree.DescendantsAndSelf(nodes, id).Concat(changedSiblings).DistinctBy(n => n.Id).ToArray();
            await tx.SaveChangesAsync(token);
            var mutation = MakeMutation(changed, nodes);
            await AppendChangesAsync(mutation, token);
            return mutation;
        }, ct);

    private Task<KnowledgeMutation> ReorderAsync(AgentMutation request, CancellationToken ct) =>
        ExecuteMutationAsync(async (tx, token) =>
        {
            var nodes = await tx.GetLiveNodesAsync(token);
            var current = nodes.Where(n => n.ParentId == request.ParentSectionId).OrderBy(n => n.Position).ToList();
            var order = request.Order ?? throw new ArgumentException("Reorder requires an explicit ordered ID list.");
            if (order.Count != current.Count || order.Select(x => x.Id).Distinct().Count() != current.Count ||
                !order.Select(x => x.Id).ToHashSet().SetEquals(current.Select(x => x.Id)))
                throw new ArgumentException("Reorder must include every sibling exactly once.");
            var byId = current.ToDictionary(n => n.Id);
            foreach (var item in order) byId[item.Id].EnsureVersion(item.ExpectedVersion);
            var changed = new List<KnowledgeNode>();
            var now = timeProvider.GetUtcNow();
            for (var i = 0; i < order.Count; i++)
            {
                var node = byId[order[i].Id];
                var version = node.Version;
                node.SetPosition(i, now);
                if (node.Version != version) changed.Add(node);
            }
            if (changed.Count == 0) return new KnowledgeMutation([]);
            await tx.SaveChangesAsync(token);
            var mutation = MakeMutation(changed, nodes);
            await AppendChangesAsync(mutation, token);
            return mutation;
        }, ct);

    private Task<KnowledgeMutation> MutateAsync(Func<IKnowledgeTransaction, IReadOnlyList<KnowledgeNode>, CancellationToken, Task<IReadOnlyList<KnowledgeNode>>> action,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(async (tx, ct) =>
        {
            var nodes = await tx.GetLiveNodesAsync(ct);
            var changed = await action(tx, nodes, ct);
            var mutation = MakeMutation(changed, nodes);
            await AppendChangesAsync(mutation, ct);
            return mutation;
        }, cancellationToken);

    private async Task<KnowledgeMutation> ExecuteMutationAsync(
        Func<IKnowledgeTransaction, CancellationToken, Task<KnowledgeMutation>> operation,
        CancellationToken cancellationToken)
    {
        var mutation = await store.InTransactionAsync(operation, cancellationToken);
        await searchPublisher.PublishAsync(mutation, cancellationToken);
        return mutation;
    }

    private async Task AppendChangesAsync(KnowledgeMutation mutation, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var node in mutation.ChangedNodes)
        {
            var payload = JsonSerializer.SerializeToElement(node, options);
            await changeJournal.AppendAsync(new EntitySnapshot("knowledge.node", node.Id, node.Version, node.Deleted, payload), cancellationToken);
        }
    }

    private static KnowledgeMutation MakeMutation(IEnumerable<KnowledgeNode> changed, IReadOnlyCollection<KnowledgeNode> all) =>
        new(changed.Select(n => n.ToView(all)).ToArray());

    private static IReadOnlyList<KnowledgeNode> Compact(IReadOnlyList<KnowledgeNode> nodes, DateTimeOffset now)
    {
        var changed = new List<KnowledgeNode>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var version = nodes[i].Version;
            nodes[i].SetPosition(i, now);
            if (nodes[i].Version != version) changed.Add(nodes[i]);
        }
        return changed;
    }

    private static KnowledgeNode Find(IReadOnlyList<KnowledgeNode> nodes, Guid id) =>
        nodes.FirstOrDefault(n => n.Id == id && n.IsActive) ?? throw new KeyNotFoundException($"Knowledge node {id} was not found.");

    private static string MakeSnippet(string markdown, string term)
    {
        var text = markdown.Replace('\n', ' ').Trim();
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        var start = index < 0 ? 0 : Math.Max(0, index - 70);
        var length = Math.Min(180, text.Length - start);
        return (start > 0 ? "…" : string.Empty) + text.Substring(start, length) + (start + length < text.Length ? "…" : string.Empty);
    }

    private static string RequiredTitle(string? title) => string.IsNullOrWhiteSpace(title)
        ? throw new ArgumentException("A title is required.") : title;
    private static long RequiredVersion(AgentMutation request) => request.ExpectedVersion
        ?? throw new ArgumentException("ExpectedVersion is required for this operation.");
    private static KnowledgeNodeType DomainKind(KnowledgeNodeKind kind) => kind == KnowledgeNodeKind.Section ? KnowledgeNodeType.Section : KnowledgeNodeType.Document;
    private static KnowledgeNodeKind AgentKind(KnowledgeNodeType kind) => kind == KnowledgeNodeType.Section ? KnowledgeNodeKind.Section : KnowledgeNodeKind.Document;
    private static KnowledgeNodeState AgentState(KnowledgeNode node, IReadOnlyCollection<KnowledgeNode> all) => new(
        AgentKind(node.Type), node.Id, node.ParentId, node.Version, node.Title, node.Type == KnowledgeNodeType.Document ? node.Markdown : null,
        node.IsDeleted ? node.Title : KnowledgeTree.GetPath(all, node), node.IsArchived, node.Position);
}
