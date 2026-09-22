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
}
