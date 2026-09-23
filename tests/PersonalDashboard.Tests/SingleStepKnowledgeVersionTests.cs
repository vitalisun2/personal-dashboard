using KnowledgeBase.Api.Application;
using KnowledgeBase.Api.Domain;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class SingleStepKnowledgeVersionTests
{
    [TestMethod]
    public async Task ManualTitleAndContentEditsToggleAndDiscardForwardVersion()
    {
        var node = new KnowledgeNode { Kind = "document", Title = "First", Content = "First body" };
        var store = new MemoryStore(new KnowledgeDocument { Nodes = [node] });
        var service = new KnowledgeService(store);
        var ct = CancellationToken.None;
        Assert.IsNull((await service.GetDocumentAsync(node.Id, ct))?.PreviousVersion);
        Assert.IsNull((await service.ToggleDocumentVersionAsync(node.Id, ct)).Document);

        Assert.IsNull(await service.RenameAsync(node.Id, "Second", ct));
        var renamed = (await service.GetDocumentAsync(node.Id, ct))!;
        Assert.AreEqual("First", renamed.PreviousVersion?.Title);
        Assert.AreEqual("First body", renamed.PreviousVersion?.Content);
        Assert.IsFalse(renamed.ShowingAlternate);

        var undone = (await service.ToggleDocumentVersionAsync(node.Id, ct)).Document!;
        Assert.AreEqual("First", undone.Title);
        Assert.AreEqual("Second", undone.PreviousVersion?.Title);
        Assert.IsTrue(undone.ShowingAlternate);

        Assert.IsNull(await service.UpdateContentAsync(node.Id, "Third body", ct));
        var edited = (await service.GetDocumentAsync(node.Id, ct))!;
        Assert.AreEqual("First", edited.PreviousVersion?.Title);
        Assert.AreEqual("First body", edited.PreviousVersion?.Content);
        Assert.AreEqual("Third body", edited.Content);
        Assert.IsFalse(edited.ShowingAlternate);
        Assert.AreEqual("First body", (await service.ToggleDocumentVersionAsync(node.Id, ct)).Document?.Content);
    }

    [TestMethod]
    public async Task ChatBatchAndConditionalContentUpdateEachRecordPreviousDocument()
    {
        var node = new KnowledgeNode { Kind = "document", Title = "Doc", Content = "A" };
        var store = new MemoryStore(new KnowledgeDocument { Nodes = [node] });
        var service = new KnowledgeService(store);
        var ct = CancellationToken.None;

        var result = await service.ApplyBatchIfCurrentAsync([
            new(node.Id, "append_content", "Doc", "A", null, null, "B")
        ], ct);
        Assert.IsTrue(result.Applied);
        Assert.AreEqual("A", (await service.GetDocumentAsync(node.Id, ct))?.PreviousVersion?.Content);
        Assert.IsNull(await service.UpdateContentIfCurrentAsync(node.Id, "Doc", "A\nB", "C", ct));
        var document = (await service.GetDocumentAsync(node.Id, ct))!;
        Assert.AreEqual("A\nB", document.PreviousVersion?.Content);
        Assert.AreEqual("C", document.Content);
        Assert.IsTrue((await service.GetTreeAsync(ct)).Single().HasPreviousVersion);
    }

    [TestMethod]
    public async Task UndoRenameRejectsSiblingTitleCollisionWithoutChangingDocument()
    {
        var first = new KnowledgeNode { Kind = "document", Title = "A", Content = "First" };
        var second = new KnowledgeNode { Kind = "document", Title = "B", Content = "Second" };
        var store = new MemoryStore(new KnowledgeDocument { Nodes = [first, second] });
        var service = new KnowledgeService(store);
        var ct = CancellationToken.None;

        Assert.IsNull(await service.RenameAsync(first.Id, "X", ct));
        Assert.IsNull(await service.RenameAsync(second.Id, "A", ct));
        var result = await service.ToggleDocumentVersionAsync(first.Id, ct);

        Assert.IsTrue(result.Found);
        Assert.IsNull(result.Document);
        Assert.Contains("названием", result.Error);
        Assert.AreEqual("X", (await service.GetDocumentAsync(first.Id, ct))?.Title);
        Assert.AreEqual("A", (await service.GetDocumentAsync(first.Id, ct))?.PreviousVersion?.Title);
        Assert.IsFalse((await service.GetDocumentAsync(first.Id, ct))!.ShowingAlternate);
    }

    private sealed class MemoryStore(KnowledgeDocument initial) : IKnowledgeStore
    {
        private KnowledgeDocument value = initial;
        public Task<KnowledgeDocument> ReadAsync(CancellationToken ct) => Task.FromResult(value);
        public Task WriteAsync(KnowledgeDocument document, CancellationToken ct)
        {
            value = document;
            return Task.CompletedTask;
        }
    }
}
