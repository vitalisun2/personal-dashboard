using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Knowledge.Domain;

var store = new MemoryKnowledgeStore();
var journal = new MemoryChangeJournal();
var publisher = new NoOpSearchPublisher();
var service = new KnowledgeService(store, journal, publisher, TimeProvider.System);

var outer = (await service.CreateAsync(KnowledgeNodeType.Section, "Project", null)).ChangedNodes.Single();
var inner = (await service.CreateAsync(KnowledgeNodeType.Section, "Research", outer.Id)).ChangedNodes.Single();
var document = (await service.CreateAsync(KnowledgeNodeType.Document, "Notes", inner.Id)).ChangedNodes.Single();
var edited = (await service.EditDocumentAsync(document.Id, "Findings", "city decoration details", document.Version)).ChangedNodes.Single();
var moved = (await service.MoveAsync(document.Id, outer.Id, "inside", edited.Version)).ChangedNodes.Single(n => n.Id == document.Id);
var renamed = (await service.RenameAsync(outer.Id, "Project Archive", outer.Version, CancellationToken.None)).ChangedNodes;
var pathAfterRename = renamed.Single(n => n.Id == document.Id);
Require(pathAfterRename.Path == "Project Archive / Findings", "renaming a section refreshes descendant document paths");
Require(pathAfterRename.Version == moved.Version, "path-only refresh preserves descendant document version");

var sectionVersion = renamed.Single(n => n.Id == outer.Id).Version;
var archived = (await service.ArchiveAsync(outer.Id, sectionVersion)).ChangedNodes;
Require((await service.GetTreeAsync()).Count == 0, "archived subtree is absent from the active tree");
Require(archived.Single(n => n.Id == document.Id).Archived && archived.Single(n => n.Id == document.Id).Version == pathAfterRename.Version + 1,
    "archive increments each descendant version");
var restored = (await service.RestoreAsync(outer.Id, archived.Single(n => n.Id == outer.Id).Version)).ChangedNodes;
Require((await service.SearchAsync("decoration")).Single().Id == document.Id, "restored document returns to active search");
var stale = false;
try { await service.EditDocumentAsync(document.Id, "Stale", "wrong", moved.Version); }
catch (KnowledgeVersionConflictException) { stale = true; }
Require(stale, "stale expected version is rejected");

var tombstones = (await service.DeleteAsync(outer.Id, restored.Single(n => n.Id == outer.Id).Version)).ChangedNodes;
Require(tombstones.All(node => node.Deleted && node.Version > 0), "permanent deletion tombstones the subtree");
Require(journal.Snapshots.Any(snapshot => snapshot.Id == document.Id && snapshot.Deleted), "sync journal contains descendant tombstone");

var syncedId = Guid.NewGuid();
var syncPayload = System.Text.Json.JsonSerializer.SerializeToElement(new KnowledgeSyncPayload(
    syncedId, "document", "Offline note", "queued body", null, 0, 1, null));
var createdSync = await service.ApplySyncOperationAsync(new SyncOperation(Guid.NewGuid(), "knowledge.node", syncedId,
    null, SyncOperationKind.Upsert, syncPayload));
Require(createdSync.Applied && createdSync.Current?.Id == syncedId && createdSync.Current.Version == 1,
    "queued create preserves the client GUID and starts at version one");
var deletedSync = await service.ApplySyncOperationAsync(new SyncOperation(Guid.NewGuid(), "knowledge.node", syncedId,
    1, SyncOperationKind.Delete, null));
Require(deletedSync.Applied && deletedSync.Current?.Deleted == true && deletedSync.Current.Version == 2,
    "queued delete accepts version one and returns a newer tombstone");
var staleSync = await service.ApplySyncOperationAsync(new SyncOperation(Guid.NewGuid(), "knowledge.node", syncedId,
    1, SyncOperationKind.Upsert, syncPayload));
Require(!staleSync.Applied && staleSync.Current?.Deleted == true, "stale queued writes return a current conflict snapshot");

var invalidParentId = Guid.NewGuid();
var invalidParentIdNode = Guid.NewGuid();
var invalidParentPayload = System.Text.Json.JsonSerializer.SerializeToElement(new KnowledgeSyncPayload(
    invalidParentIdNode, "section", "Orphan", null, invalidParentId, 0, 1, null));
var invalidParentSync = await service.ApplySyncOperationAsync(new SyncOperation(Guid.NewGuid(), "knowledge.node", invalidParentIdNode,
    null, SyncOperationKind.Upsert, invalidParentPayload));
Require(!invalidParentSync.Applied && invalidParentSync.Current is null && !string.IsNullOrWhiteSpace(invalidParentSync.ConflictReason),
    "invalid parent is returned as a stable sync rejection");

var invalidTitleNode = Guid.NewGuid();
var invalidTitlePayload = System.Text.Json.JsonSerializer.SerializeToElement(new KnowledgeSyncPayload(
    invalidTitleNode, "document", new string('x', 301), "body", null, 0, 1, null));
var invalidTitleSync = await service.ApplySyncOperationAsync(new SyncOperation(Guid.NewGuid(), "knowledge.node", invalidTitleNode,
    null, SyncOperationKind.Upsert, invalidTitlePayload));
Require(!invalidTitleSync.Applied && invalidTitleSync.Current is null && !string.IsNullOrWhiteSpace(invalidTitleSync.ConflictReason),
    "invalid title is returned as a stable sync rejection");

var archivedNode = (await service.CreateAsync(KnowledgeNodeType.Document, "Archived note", null)).ChangedNodes.Single();
var archiveResult = (await service.ArchiveAsync(archivedNode.Id, archivedNode.Version)).ChangedNodes.Single();
var archivedPayload = System.Text.Json.JsonSerializer.SerializeToElement(new KnowledgeSyncPayload(
    archiveResult.Id, "document", archiveResult.Title, archiveResult.Markdown, archiveResult.ParentId,
    archiveResult.Position, archiveResult.Version, archiveResult.Path, Archived: true));
var archivedUpsert = await service.ApplySyncOperationAsync(new SyncOperation(Guid.NewGuid(), "knowledge.node", archivedNode.Id,
    archiveResult.Version, SyncOperationKind.Upsert, archivedPayload));
Require(!archivedUpsert.Applied && archivedUpsert.Current?.Version == archiveResult.Version,
    "upserts cannot silently edit an archived node");
Console.WriteLine("Knowledge scenario passed: nested create/edit/move/rename/archive/restore/delete and stale version conflict.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Scenario failed: {message}");
}

sealed class MemoryKnowledgeStore : IKnowledgeStore
{
    private readonly List<KnowledgeNode> _nodes = [];

    public Task<TResult> InTransactionAsync<TResult>(Func<IKnowledgeTransaction, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken) =>
        operation(new MemoryKnowledgeTransaction(_nodes), cancellationToken);

    private sealed class MemoryKnowledgeTransaction(List<KnowledgeNode> nodes) : IKnowledgeTransaction
    {
        public Task<IReadOnlyList<KnowledgeNode>> GetLiveNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeNode>>(nodes.Where(node => node.IsActive).ToArray());

        public Task<IReadOnlyList<KnowledgeNode>> GetAllNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeNode>>(nodes.ToArray());

        public Task AddAsync(KnowledgeNode node, CancellationToken cancellationToken)
        {
            nodes.Add(node);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

sealed class MemoryChangeJournal : IEntityChangeJournal
{
    public List<EntitySnapshot> Snapshots { get; } = [];
    private long _sequence;

    public Task<long> AppendAsync(EntitySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Snapshots.Add(snapshot);
        return Task.FromResult(++_sequence);
    }

    public Task<EntityChangePage> ReadAfterAsync(long sequence, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EntityChangePage([], sequence, true));
}

sealed class NoOpSearchPublisher : IKnowledgeSearchPublisher
{
    public Task PublishAsync(KnowledgeMutation mutation, CancellationToken cancellationToken) => Task.CompletedTask;
}
