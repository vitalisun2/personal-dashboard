using AgentChat;
using KnowledgeBase.Api.Application;
using KnowledgeBase.Api.Domain;

namespace PersonalDashboard.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ChatIntentRoutingRegressionTests
{
    [TestMethod]
    public async Task ExactBroccoliRequestCreatesMissingSectionAndDocumentInOneConfirmedWrite()
    {
        const string request = "Так, добавь документ под названием Брокколи как источник кемпферана в раздел Здоровье";
        var router = new RecordingKnowledgeRouter(new KnowledgeIntent(
            "create_document", null, "Брокколи", "Источник кемпферана", "Здоровье", null));
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument
        {
            Nodes = [new KnowledgeNode { Kind = "section", Title = "Работа" }]
        });
        var facade = new KnowledgeChatFacade(new KnowledgeService(store), router);

        var reply = await facade.HandleAsync(new ChatTurn(request, true, null, []), CancellationToken.None);

        Assert.AreEqual(request, router.LastRequest);
        Assert.IsTrue(reply.NeedsClarification, reply.Text);
        Assert.IsFalse(reply.ChangedData, "The preview must not report a completed write before confirmation.");
        Assert.Contains("Здоровье", reply.Text);
        Assert.Contains("Брокколи", reply.Text);
        Assert.IsNotNull(reply.Pending, "Both creates must be proposed for confirmation.");
        var pending = KnowledgeCommandPlanner.ReadPending(reply.Pending);
        Assert.IsNotNull(pending);
        Assert.AreEqual(KnowledgeCommandKind.CreateSectionAndDocument, pending.Kind);
        Assert.AreEqual("Здоровье", pending.SectionTitle);
        Assert.AreEqual("Брокколи", pending.Title);
        Assert.AreEqual("Источник кемпферана", pending.Content);
        Assert.AreEqual(0, store.Writes, "Previewing both creates must not write to the knowledge base.");
        Assert.HasCount(1, store.Value.Nodes);

        var committed = await facade.HandleAsync(new ChatTurn("да", false, reply.Pending, []), CancellationToken.None);

        Assert.IsTrue(committed.ChangedData, committed.Text);
        Assert.AreEqual(1, store.Writes, "Creating the section and document must be one atomic store write.");
        Assert.HasCount(3, store.Value.Nodes);
        var createdSection = store.Value.Nodes.Single(node => node.Kind == "section" && node.Title == "Здоровье");
        var createdDocument = store.Value.Nodes.Single(node => node.Kind == "document" && node.Title == "Брокколи");
        Assert.AreEqual("Источник кемпферана", createdDocument.Content);
        Assert.AreEqual(createdSection.Id, createdDocument.ParentId);
    }

    [TestMethod]
    public async Task MissingSectionCreatedAfterPreviewRejectsCombinedCreateWithoutPartialDocument()
    {
        const string request = "Так, добавь документ под названием Брокколи как источник кемпферана в раздел Здоровье";
        var router = new RecordingKnowledgeRouter(new KnowledgeIntent(
            "create_document", null, "Брокколи", "Источник кемпферана", "Здоровье", null));
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument());
        var facade = new KnowledgeChatFacade(new KnowledgeService(store), router);

        var preview = await facade.HandleAsync(new ChatTurn(request, true, null, []), CancellationToken.None);
        Assert.IsNotNull(preview.Pending, preview.Text);

        store.Value.Nodes.Add(new KnowledgeNode { Kind = "section", Title = "Здоровье" });
        var committed = await facade.HandleAsync(new ChatTurn("да", false, preview.Pending, []), CancellationToken.None);

        Assert.IsFalse(committed.ChangedData, committed.Text);
        Assert.AreEqual(0, store.Writes, "A conflicting section must reject the operation before creating either node.");
        Assert.HasCount(1, store.Value.Nodes);
        Assert.IsFalse(store.Value.Nodes.Any(node => node.Kind == "document" && node.Title == "Брокколи"));
    }

    [TestMethod]
    public async Task KnowledgeBatchWithOneUnresolvedTargetProducesNoPreviewOrWrites()
    {
        var router = new RecordingKnowledgeRouter(new KnowledgeIntent("batch_update", null, null, null, null, null,
            Operations:
            [
                new KnowledgeIntentOperation("append_document", "Декор", null, "Новый абзац", null),
                new KnowledgeIntentOperation("replace_document", "Несуществующий документ", null, "Новый текст", null)
            ]));
        var document = new KnowledgeNode { Kind = "document", Title = "Декор", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeDocument { Nodes = [document] });
        var facade = new KnowledgeChatFacade(new KnowledgeService(store), router);

        var reply = await facade.HandleAsync(new ChatTurn("Обнови сразу два документа", true, null, []), CancellationToken.None);

        Assert.IsTrue(reply.NeedsClarification, reply.Text);
        Assert.IsNotNull(reply.Pending, "The assistant may clarify the unresolved batch target.");
        var pending = KnowledgeCommandPlanner.ReadPending(reply.Pending);
        Assert.IsNotNull(pending);
        Assert.AreEqual(KnowledgeCommandKind.None, pending.Kind, "An invalid batch must not leave an executable partial batch preview.");
        Assert.Contains("Ничего не записано", reply.Text);
        Assert.AreEqual("Исходный текст", document.Content);
        Assert.AreEqual(0, store.Writes);
    }

    private sealed class RecordingKnowledgeRouter(KnowledgeIntent intent) : IChatResponder, IKnowledgeIntentRouter
    {
        public string? LastRequest { get; private set; }

        public Task<string> ReplyAsync(string text, IReadOnlyList<ChatMessage>? history, CancellationToken cancellationToken) =>
            Task.FromResult("Ответ");

        public Task<(string Status, KnowledgeIntent? Intent)> ClassifyAsync(
            string text, IReadOnlyList<ChatMessage>? history, CancellationToken cancellationToken)
        {
            LastRequest = text;
            return Task.FromResult<(string, KnowledgeIntent?)>(("ok", intent));
        }

        public Task<(string Status, KnowledgeIntent? Intent)> ClassifyAsync(
            string text, IReadOnlyList<ChatMessage>? history, string scopedContext, CancellationToken cancellationToken) =>
            ClassifyAsync(text, history, cancellationToken);

        public Task<(string Status, string Decision, double Confidence)> ClassifyConfirmationAsync(
            string preview, string answer, CancellationToken cancellationToken) =>
            Task.FromResult(("ok", "approve", 0.99));
    }

    private sealed class InMemoryKnowledgeStore(KnowledgeDocument value) : IKnowledgeStore
    {
        public KnowledgeDocument Value { get; private set; } = value;
        public int Writes { get; private set; }

        public Task<KnowledgeDocument> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);

        public Task WriteAsync(KnowledgeDocument document, CancellationToken cancellationToken)
        {
            Value = document;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
