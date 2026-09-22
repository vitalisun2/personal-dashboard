using KnowledgeBase.Api.Application;
using KnowledgeBase.Api.Domain;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class KnowledgeServiceTests
{
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
