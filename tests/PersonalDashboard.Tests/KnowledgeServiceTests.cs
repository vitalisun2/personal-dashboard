using KnowledgeBase.Api.Application;
using KnowledgeBase.Api.Domain;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class KnowledgeServiceTests
{
    [TestMethod]
    public async Task UntitledDocumentsKeepIncreasingAfterDeletion()
    {
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument());
        var service = new KnowledgeService(store);

        var first = await service.CreateAsync("document", null, null, null, CancellationToken.None);
        var second = await service.CreateAsync("document", null, null, null, CancellationToken.None);
        await service.DeleteAsync(second.Node!.Id, CancellationToken.None);
        var third = await service.CreateAsync("document", null, null, null, CancellationToken.None);

        Assert.AreEqual("Doc 1", first.Node?.Title);
        Assert.AreEqual("", first.Node?.Content);
        Assert.AreEqual("Doc 2", second.Node?.Title);
        Assert.AreEqual("Doc 3", third.Node?.Title);
        Assert.AreEqual(4, store.Value.NextDocumentNumber);
    }

    [TestMethod]
    public async Task PeerImportPreservesIdsAndDeletesSubtreesIdempotently()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var parent = new KnowledgeNodeDto(parentId, "section", "Imported", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var child = new KnowledgeNodeDto(childId, "document", "Document", parentId, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument());
        var outbox = new RecordingKnowledgeSyncOutbox();
        var service = new KnowledgeService(store, outbox);

        Assert.IsFalse(await service.ImportSyncNodeAsync(child, "markdown", false, CancellationToken.None), "An absent parent must reject the import without writing a partial node.");
        Assert.AreEqual(0, store.Value.Nodes.Count);
        Assert.AreEqual(0, outbox.Operations.Count, "Rejected or peer-originated imports must not enqueue outbound echo.");
        Assert.IsTrue(await service.ImportSyncNodeAsync(parent, null, false, CancellationToken.None));
        Assert.IsTrue(await service.ImportSyncNodeAsync(child, "markdown", false, CancellationToken.None));
        Assert.IsTrue(await service.ImportSyncNodeAsync(child, "markdown", false, CancellationToken.None));
        Assert.AreEqual(2, store.Value.Nodes.Count);
        Assert.AreEqual(childId, store.Value.Nodes.Single(node => node.Title == "Document").Id);
        Assert.IsTrue(await service.ImportSyncNodeAsync(parent, null, true, CancellationToken.None));
        Assert.IsTrue(await service.ImportSyncNodeAsync(parent, null, true, CancellationToken.None));
        Assert.AreEqual(0, store.Value.Nodes.Count);
        Assert.AreEqual(0, outbox.Operations.Count, "Peer-originated creates, updates and deletes must not echo.");
    }

    [TestMethod]
    public async Task UntitledDocumentSkipsExistingNameFromOlderData()
    {
        var first = new KnowledgeNode { Kind = "document", Title = "Doc 1" };
        var last = new KnowledgeNode { Kind = "document", Title = "Doc 7" };
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument { Nodes = [first, last] });
        var service = new KnowledgeService(store);

        var created = await service.CreateAsync("document", null, null, null, CancellationToken.None);

        Assert.AreEqual("Doc 8", created.Node?.Title);
    }

    [TestMethod]
    public async Task UntitledSectionsKeepIncreasingAfterDeletion()
    {
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument());
        var service = new KnowledgeService(store);

        var first = await service.CreateAsync("section", null, null, null, CancellationToken.None);
        var second = await service.CreateAsync("section", null, null, null, CancellationToken.None);
        await service.DeleteAsync(second.Node!.Id, CancellationToken.None);
        var third = await service.CreateAsync("section", null, null, null, CancellationToken.None);

        Assert.AreEqual("Section 1", first.Node?.Title);
        Assert.AreEqual("Section 2", second.Node?.Title);
        Assert.AreEqual("Section 3", third.Node?.Title);
        Assert.AreEqual(4, store.Value.NextSectionNumber);
    }

    [TestMethod]
    public async Task UntitledSectionSkipsExistingNameFromOlderData()
    {
        var first = new KnowledgeNode { Kind = "section", Title = "Section 1" };
        var last = new KnowledgeNode { Kind = "section", Title = "Section 7" };
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument { Nodes = [first, last] });
        var service = new KnowledgeService(store);

        var created = await service.CreateAsync("section", null, null, null, CancellationToken.None);

        Assert.AreEqual("Section 8", created.Node?.Title);
    }

    [TestMethod]
    public async Task MoveRejectsCycleAndKeepsTreeIntact()
    {
        var parent = new KnowledgeNode { Kind = "section", Title = "Parent" };
        var child = new KnowledgeNode { Kind = "section", Title = "Child", ParentId = parent.Id };
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument { Nodes = [parent, child] });
        var service = new KnowledgeService(store);

        var error = await service.MoveAsync(parent.Id, child.Id, 0, CancellationToken.None);

        Assert.AreEqual("Нельзя переместить узел внутрь самого себя.", error);
        Assert.IsNull(store.Value.Nodes.Single(node => node.Id == parent.Id).ParentId);
        Assert.AreEqual(parent.Id, store.Value.Nodes.Single(node => node.Id == child.Id).ParentId);
    }

    private sealed class InMemoryKnowledgeStore(KnowledgeDocument value) : IKnowledgeStore
    {
        public KnowledgeDocument Value { get; private set; } = value;
        public Task<KnowledgeDocument> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task WriteAsync(KnowledgeDocument document, CancellationToken cancellationToken)
        {
            Value = document;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingKnowledgeSyncOutbox : IKnowledgeSyncOutbox
    {
        public List<string> Operations { get; } = [];

        public Task EnqueueAsync(string type, Guid id, string operation, System.Text.Json.JsonElement? payload, CancellationToken cancellationToken)
        {
            Operations.Add($"{type}:{id}:{operation}");
            return Task.CompletedTask;
        }
    }
}
