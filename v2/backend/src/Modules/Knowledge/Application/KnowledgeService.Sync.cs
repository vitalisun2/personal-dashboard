using System.Text.Json;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Application;

public sealed partial class KnowledgeService
{
    private static readonly JsonSerializerOptions SyncJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Applies a queued full-state entity operation; the caller owns the surrounding sync transaction.</summary>
    public async Task<SyncMutationResult> ApplySyncOperationAsync(SyncOperation operation, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation.Type, "knowledge.node", StringComparison.Ordinal))
            return new SyncMutationResult(false, null, "Unsupported Knowledge sync entity type.");

        try
        {
            return await store.InTransactionAsync(async (tx, ct) =>
            {
            var all = await tx.GetAllNodesAsync(ct);
            var existing = all.FirstOrDefault(n => n.Id == operation.Id);
            var root = existing is { IsDeleted: false } ? existing : null;
            EntitySnapshot? Current() => existing is null ? null : Snapshot(existing, all);

            if (operation.Kind == SyncOperationKind.Delete)
            {
                if (root is null) return new SyncMutationResult(false, Current(), "Knowledge node was not found or was permanently deleted.");
                if (operation.ExpectedVersion is not long deleteVersion)
                    return new SyncMutationResult(false, Snapshot(root, all), "ExpectedVersion is required for deletion.");
                if (root.Version != deleteVersion)
                    return new SyncMutationResult(false, Snapshot(root, all), $"Expected version {deleteVersion}, current version is {root.Version}.");

                var subtree = KnowledgeTree.NonDeletedDescendantsAndSelf(all, root.Id);
                var siblings = all.Where(n => n.ParentId == root.ParentId && n.Id != root.Id && n.IsActive).OrderBy(n => n.Position).ToList();
                root.Delete(deleteVersion, timeProvider.GetUtcNow());
                foreach (var child in subtree.Skip(1)) child.Delete(child.Version, timeProvider.GetUtcNow());
                var changedSiblings = Compact(siblings, timeProvider.GetUtcNow());
                await tx.SaveChangesAsync(ct);
                var mutation = MakeMutation(subtree.Concat(changedSiblings), all);
                await AppendChangesAsync(mutation, ct);
                return new SyncMutationResult(true, Snapshot(root, all), null);
            }

            if (operation.Kind != SyncOperationKind.Upsert)
                return new SyncMutationResult(false, Current(), "Unsupported sync operation.");
            if (operation.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
                return new SyncMutationResult(false, Current(), "An upsert requires a Knowledge payload.");

            var payload = payloadElement.Deserialize<KnowledgeSyncPayload>(SyncJsonOptions);
            if (payload is null || payload.Id != operation.Id)
                return new SyncMutationResult(false, Current(), "Payload ID must match the operation ID.");
            if (payload.Kind is not ("section" or "document"))
                return new SyncMutationResult(false, Current(), "Kind must be section or document.");
            if (string.IsNullOrWhiteSpace(payload.Title))
                return new SyncMutationResult(false, Current(), "A title is required.");

            var domainType = payload.Kind == "section" ? KnowledgeNodeType.Section : KnowledgeNodeType.Document;
            var markdown = payload.Kind == "document" ? payload.Markdown ?? string.Empty : string.Empty;
            if (payload.Position < 0)
                return new SyncMutationResult(false, Current(), "Position cannot be negative.");

            if (root is null)
            {
                if (existing is not null)
                    return new SyncMutationResult(false, Current(), "A permanently deleted Knowledge ID cannot be reused.");
                if (operation.ExpectedVersion is not null)
                    return new SyncMutationResult(false, null, "Create must not include ExpectedVersion.");
                if (payload.Archived)
                    return new SyncMutationResult(false, null, "Create an active node before archiving it.");
                var active = all.Where(n => n.IsActive).ToList();
                KnowledgeTree.EnsureValidParent(active, payload.ParentId);
                var siblings = active.Where(n => n.ParentId == payload.ParentId).OrderBy(n => n.Position).ToList();
                var position = Math.Clamp(payload.Position, 0, siblings.Count);
                var node = KnowledgeNode.Create(domainType, payload.Title, payload.ParentId, position, timeProvider.GetUtcNow(), operation.Id, markdown);
                siblings.Insert(position, node);
                var changedSiblings = Compact(siblings, timeProvider.GetUtcNow());
                await tx.AddAsync(node, ct);
                await tx.SaveChangesAsync(ct);
                var mutation = MakeMutation([node, .. changedSiblings], active.Append(node).ToArray());
                await AppendChangesAsync(mutation, ct);
                return new SyncMutationResult(true, Snapshot(node, active.Append(node).ToArray()), null);
            }

            if (root.Type != domainType)
                return new SyncMutationResult(false, Snapshot(root, all), "Node kind cannot be changed.");
            if (operation.ExpectedVersion is not long expectedVersion)
                return new SyncMutationResult(false, Snapshot(root, all), "ExpectedVersion is required for an update.");
            if (root.Version != expectedVersion)
                return new SyncMutationResult(false, Snapshot(root, all), $"Expected version {expectedVersion}, current version is {root.Version}.");

            if (payload.Archived != root.IsArchived)
            {
                var sameState = root.Title == payload.Title.Trim() && root.Markdown == markdown &&
                    root.ParentId == payload.ParentId && root.Position == payload.Position;
                if (!sameState)
                    return new SyncMutationResult(false, Snapshot(root, all), "Archive or restore must be sent as a separate Knowledge operation.");
                var subtree = root.IsArchived
                    ? KnowledgeTree.ArchivedDescendantsAndSelf(all, root.Id)
                    : KnowledgeTree.DescendantsAndSelf(all, root.Id);
                if (root.IsArchived) KnowledgeTree.EnsureValidParent(all, root.ParentId);
                var siblings = all.Where(n => n.ParentId == root.ParentId && n.Id != root.Id && n.IsActive).OrderBy(n => n.Position).ToList();
                var now = timeProvider.GetUtcNow();
                if (root.IsArchived)
                {
                    root.Restore(expectedVersion, now);
                    foreach (var child in subtree.Skip(1)) child.Restore(child.Version, now);
                    siblings.Insert(Math.Clamp(root.Position, 0, siblings.Count), root);
                }
                else
                {
                    root.Archive(expectedVersion, now);
                    foreach (var child in subtree.Skip(1)) child.Archive(child.Version, now);
                }
                var changedSiblings = Compact(siblings, now);
                await tx.SaveChangesAsync(ct);
                var mutation = MakeMutation(subtree.Concat(changedSiblings), all);
                await AppendChangesAsync(mutation, ct);
                return new SyncMutationResult(true, Snapshot(root, all), null);
            }

            if (root.IsArchived)
                return new SyncMutationResult(false, Snapshot(root, all), "Archived Knowledge nodes cannot be edited; restore the node first.");

            var live = all.Where(n => n.IsActive).ToList();
            try { KnowledgeTree.EnsureValidParent(live, payload.ParentId, root.Id); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            { return new SyncMutationResult(false, Snapshot(root, all), ex.Message); }
            var oldParent = root.ParentId;
            var pathChanged = oldParent != payload.ParentId || root.Title != payload.Title.Trim();
            var oldSiblings = live.Where(n => n.ParentId == oldParent && n.Id != root.Id).OrderBy(n => n.Position).ToList();
            var newSiblings = live.Where(n => n.ParentId == payload.ParentId && n.Id != root.Id).OrderBy(n => n.Position).ToList();
            var targetPosition = Math.Clamp(payload.Position, 0, newSiblings.Count);
            newSiblings.Insert(targetPosition, root);
            var nowUpdate = timeProvider.GetUtcNow();
            root.ApplySyncState(payload.Title, markdown, payload.ParentId, targetPosition, expectedVersion, nowUpdate);
            var changedSiblingsUpdate = Compact(oldSiblings, nowUpdate).Concat(Compact(newSiblings, nowUpdate));
            var affected = new List<KnowledgeNode> { root };
            if (pathChanged && root.Type == KnowledgeNodeType.Section)
                affected.AddRange(KnowledgeTree.DescendantsAndSelf(live, root.Id).Skip(1));
            var changedNodes = affected.Concat(changedSiblingsUpdate).DistinctBy(n => n.Id).ToArray();
            await tx.SaveChangesAsync(ct);
            var changedMutation = MakeMutation(changedNodes, all);
            await AppendChangesAsync(changedMutation, ct);
            return new SyncMutationResult(true, Snapshot(root, all), null);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            // Let the transaction runner observe the exception first so it can roll back any
            // domain changes; then return a stable conflict snapshot to the sync caller.
            var current = await store.InTransactionAsync(async (tx, ct) =>
            {
                var all = await tx.GetAllNodesAsync(ct);
                var node = all.FirstOrDefault(candidate => candidate.Id == operation.Id);
                return node is null ? null : Snapshot(node, all);
            }, cancellationToken);
            return new SyncMutationResult(false, current, ex.Message);
        }
    }

    private static EntitySnapshot Snapshot(KnowledgeNode node, IReadOnlyCollection<KnowledgeNode> all)
    {
        JsonElement? payload = node.IsDeleted ? null : JsonSerializer.SerializeToElement(node.ToView(all), SyncJsonOptions);
        return new EntitySnapshot("knowledge.node", node.Id, node.Version, node.IsDeleted, payload);
    }
}

public sealed record KnowledgeSyncPayload(
    Guid Id,
    string Kind,
    string Title,
    string? Markdown,
    Guid? ParentId,
    int Position,
    long Version,
    string? Path,
    bool Archived = false);
