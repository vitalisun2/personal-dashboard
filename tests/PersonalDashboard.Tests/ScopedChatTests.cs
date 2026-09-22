using AC = AgentChat;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class ScopedChatTests
{
    [TestMethod]
    public async Task SessionCannotBeReadOrContinuedThroughAnotherScope()
    {
        var store = new MemoryChatStore();
        var tasks = new ProbeFacade(AC.ChatScope.Tasks);
        var knowledge = new ProbeFacade(AC.ChatScope.Knowledge);
        var service = new AC.ChatService(store, [tasks, knowledge]);

        var empty = await service.SendAsync(null, AC.ChatScope.Knowledge, new AC.ChatMessageRequest(null), CancellationToken.None);
        Assert.IsNotNull(empty.Session);
        Assert.IsNull(empty.Reply);

        var created = await service.SendAsync(null, AC.ChatScope.Tasks, new AC.ChatMessageRequest("создай задачу"), CancellationToken.None);
        Assert.IsNotNull(created.Session);
        Assert.IsNull(await service.GetAsync(created.Session!.Id, AC.ChatScope.Knowledge, CancellationToken.None));
        var crossScope = await service.SendAsync(created.Session.Id, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("создай документ"), CancellationToken.None);
        Assert.AreEqual("Сессия не найдена в этой области.", crossScope.Error);
        Assert.AreEqual(1, tasks.Calls);
        Assert.AreEqual(0, knowledge.Calls);
    }

    [TestMethod]
    public async Task EphemeralSessionIsNotSharedWithASecondStore()
    {
        var store = new AC.EphemeralChatSessionStore();
        var session = await store.CreateAsync(AC.ChatScope.Tasks, "привет", CancellationToken.None);

        Assert.IsNotNull(await store.GetAsync(session.Id, CancellationToken.None));
        Assert.IsNull(await new AC.EphemeralChatSessionStore().GetAsync(session.Id, CancellationToken.None));
    }

    [TestMethod]
    public void KnowledgePlannerSeparatesTitleContentAndSection()
    {
        var plan = KnowledgeCommandPlanner.Plan("Создай документ План с содержанием Текст в разделе Работа");

        Assert.AreEqual(KnowledgeCommandKind.CreateDocument, plan.Kind);
        Assert.AreEqual("План", plan.Title);
        Assert.AreEqual("Текст", plan.Content);
        Assert.AreEqual("Работа", plan.SectionTitle);
        Assert.IsNull(plan.Missing);
    }

    [TestMethod]
    public async Task KnowledgeClarificationContinuesThePendingCreate()
    {
        var section = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "section", Title = "Работа" };
        var knowledgeStore = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [section] });
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(knowledgeStore), new FixedResponder());
        var chat = new AC.ChatService(new MemoryChatStore(), [new ProbeFacade(AC.ChatScope.Tasks), facade]);

        var pending = await chat.SendAsync(null, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("Создай документ с содержанием Текст в разделе Работа"), CancellationToken.None);
        Assert.IsTrue(pending.Reply!.NeedsClarification);
        Assert.IsNotNull(pending.Session!.Pending);

        var completed = await chat.SendAsync(pending.Session.Id, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("План"), CancellationToken.None);
        Assert.IsTrue(completed.Reply!.NeedsClarification);
        Assert.AreEqual(0, knowledgeStore.Writes);
        var confirmed = await chat.SendAsync(pending.Session.Id, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("да, подтверждаю"), CancellationToken.None);
        Assert.IsFalse(confirmed.Reply!.NeedsClarification);
        Assert.IsTrue(confirmed.Reply.ChangedData);
        var created = knowledgeStore.Value.Nodes.Single(node => !node.IsSection);
        Assert.AreEqual("План", created.Title);
        Assert.AreEqual("Текст", created.Content);
        Assert.AreEqual(section.Id, created.ParentId);
    }

    [TestMethod]
    public async Task ContentMutationShowsFullPreviewWithoutWriting()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var responder = new CountingResponder();
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), responder);

        var reply = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Нужно добавить по 2 дерева для каждой локации", true, null, []), CancellationToken.None);

        Assert.IsFalse(reply.ChangedData);
        Assert.IsTrue(reply.NeedsClarification);
        Assert.IsNotNull(reply.Pending);
        Assert.Contains("Документ: «Doc 4»", reply.Text);
        Assert.Contains("Операция: добавить", reply.Text);
        Assert.Contains("Исходный текст" + Environment.NewLine + "Нужно добавить по 2 дерева для каждой локации", reply.Text);
        Assert.AreEqual("Исходный текст", store.Value.Nodes.Single().Content);
        Assert.AreEqual(0, store.Writes);
        Assert.AreEqual(0, responder.Calls);
    }

    [TestMethod]
    public async Task PositiveConfirmationCommitsAndVerifiesPreviewedMutation()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), new CountingResponder());
        var preview = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Нужно добавить по 2 дерева для каждой локации", true, null, []), CancellationToken.None);

        var committed = await facade.HandleAsync(new AC.ChatTurn("да, подтверждаю", false, preview.Pending, []), CancellationToken.None);

        Assert.IsTrue(committed.ChangedData, committed.Text);
        Assert.IsFalse(committed.NeedsClarification);
        Assert.AreEqual("Исходный текст" + Environment.NewLine + "Нужно добавить по 2 дерева для каждой локации", store.Value.Nodes.Single().Content);
        Assert.AreEqual(1, store.Writes);
        Assert.Contains("проверено", committed.Text);
    }

    [TestMethod]
    public async Task RejectionCancelsPreviewWithoutMutation()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), new CountingResponder());
        var preview = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Новый текст", true, null, []), CancellationToken.None);

        var rejected = await facade.HandleAsync(new AC.ChatTurn("Нет, не надо", false, preview.Pending, []), CancellationToken.None);

        Assert.IsFalse(rejected.NeedsClarification, rejected.Text);
        Assert.IsFalse(rejected.ChangedData);
        Assert.IsNull(rejected.Pending);
        Assert.AreEqual("Исходный текст", document.Content);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task AmbiguousConfirmationKeepsPreviewPendingWithoutMutation()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), new CountingResponder());
        var preview = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Новый текст", true, null, []), CancellationToken.None);

        var unclear = await facade.HandleAsync(new AC.ChatTurn("А что сейчас записано?", false, preview.Pending, []), CancellationToken.None);

        Assert.IsTrue(unclear.NeedsClarification);
        Assert.IsNotNull(unclear.Pending);
        Assert.Contains("Предпросмотр", unclear.Text);
        Assert.AreEqual("Исходный текст", document.Content);
        Assert.AreEqual(0, store.Writes);

        var confirmed = await facade.HandleAsync(new AC.ChatTurn("да, подтверждаю", false, unclear.Pending, []), CancellationToken.None);
        Assert.IsTrue(confirmed.ChangedData, confirmed.Text);
        Assert.AreEqual("Исходный текст" + Environment.NewLine + "Новый текст", document.Content);
        Assert.AreEqual(1, store.Writes);
    }

    [TestMethod]
    public async Task StaleContentPreviewRefreshesBeforeAnyWriteAndRequiresNewApproval()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), new CountingResponder());
        var preview = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Новый текст", true, null, []), CancellationToken.None);
        document.Content = "Внешнее обновление";

        var refreshed = await facade.HandleAsync(new AC.ChatTurn("да, подтверждаю", false, preview.Pending, []), CancellationToken.None);

        Assert.IsTrue(refreshed.NeedsClarification);
        Assert.IsNotNull(refreshed.Pending);
        Assert.Contains("Данные изменились", refreshed.Text);
        Assert.Contains("Внешнее обновление" + Environment.NewLine + "Новый текст", refreshed.Text);
        Assert.AreEqual("Внешнее обновление", document.Content);
        Assert.AreEqual(0, store.Writes);

        var committed = await facade.HandleAsync(new AC.ChatTurn("подтверждаю", false, refreshed.Pending, []), CancellationToken.None);
        Assert.IsTrue(committed.ChangedData, committed.Text);
        Assert.AreEqual("Внешнее обновление" + Environment.NewLine + "Новый текст", document.Content);
        Assert.AreEqual(1, store.Writes);
    }

    [TestMethod]
    public async Task FreeFormPositiveConfirmationCommitsWhenRouterIsConfident()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var responder = new ReviewRouterResponder("approve", 0.99);
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), responder);
        var preview = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Новый текст", true, null, []), CancellationToken.None);

        var result = await facade.HandleAsync(new AC.ChatTurn("Да, этот вариант меня устраивает, применяй", false, preview.Pending, []), CancellationToken.None);

        Assert.IsTrue(result.ChangedData, result.Text);
        Assert.AreEqual("Исходный текст" + Environment.NewLine + "Новый текст", document.Content);
        Assert.AreEqual(1, store.Writes);
        Assert.Contains("Документ: «Doc 4»", responder.LastPreview);
    }

    [TestMethod]
    public async Task FreeFormRejectionCancelsWithoutMutation()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var responder = new ReviewRouterResponder("reject", 0.98);
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), responder);
        var preview = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Новый текст", true, null, []), CancellationToken.None);

        var result = await facade.HandleAsync(new AC.ChatTurn("Я передумал, оставь документ как был", false, preview.Pending, []), CancellationToken.None);

        Assert.IsFalse(result.ChangedData);
        Assert.IsNull(result.Pending);
        Assert.AreEqual("Исходный текст", document.Content);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task OrdinaryKnowledgeQuestionUsesResponderWithoutMutation()
    {
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument());
        var responder = new CountingResponder();
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), responder);

        var reply = await facade.HandleAsync(new AC.ChatTurn("Как лучше организовать заметки?", true, null, []), CancellationToken.None);

        Assert.AreEqual("ответ модели", reply.Text);
        Assert.AreEqual(1, responder.Calls);
        Assert.AreEqual(0, store.Writes);
        Assert.IsFalse(reply.ChangedData);
    }

    [TestMethod]
    public async Task AmbiguousDocumentDestinationAsksClarificationWithoutMutation()
    {
        var first = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Первый" };
        var second = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Doc 4", Content = "Второй" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [first, second] });
        var responder = new CountingResponder();
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(store), responder);

        var reply = await facade.HandleAsync(new AC.ChatTurn("Добавь в документ док 4 текст следующего содержания: Новый текст", true, null, []), CancellationToken.None);

        Assert.IsTrue(reply.NeedsClarification);
        Assert.IsNotNull(reply.Pending);
        Assert.Contains("несколько документов", reply.Text);
        Assert.AreEqual(0, store.Writes);
        Assert.AreEqual(0, responder.Calls);
        Assert.AreEqual("Первый", first.Content);
        Assert.AreEqual("Второй", second.Content);
    }

    [TestMethod]
    public async Task ConversationHistoryIsScopedToTheCurrentSessionScope()
    {
        var tasks = new HistoryFacade(AC.ChatScope.Tasks);
        var knowledge = new HistoryFacade(AC.ChatScope.Knowledge);
        var chat = new AC.ChatService(new MemoryChatStore(), [tasks, knowledge]);

        var taskSession = await chat.SendAsync(null, AC.ChatScope.Tasks, new AC.ChatMessageRequest("первая задача"), CancellationToken.None);
        await chat.SendAsync(taskSession.Session!.Id, AC.ChatScope.Tasks, new AC.ChatMessageRequest("и продолжение"), CancellationToken.None);
        await chat.SendAsync(null, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("секрет базы знаний"), CancellationToken.None);

        CollectionAssert.Contains(tasks.LastHistory.Select(x => x.Text).ToList(), "первая задача");
        CollectionAssert.Contains(tasks.LastHistory.Select(x => x.Text).ToList(), "и продолжение");
        CollectionAssert.DoesNotContain(knowledge.LastHistory.Select(x => x.Text).ToList(), "первая задача");
        CollectionAssert.Contains(knowledge.LastHistory.Select(x => x.Text).ToList(), "секрет базы знаний");
    }

    [TestMethod]
    public async Task ConversationKeepsAllTwentyUserTurnsAndThenStops()
    {
        var facade = new HistoryFacade(AC.ChatScope.Knowledge);
        var chat = new AC.ChatService(new AC.EphemeralChatSessionStore(), [facade]);
        var first = await chat.SendAsync(null, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("первая реплика"), CancellationToken.None);
        var id = first.Session!.Id;
        for (var turn = 2; turn <= 20; turn++)
            await chat.SendAsync(id, AC.ChatScope.Knowledge, new AC.ChatMessageRequest($"реплика {turn}"), CancellationToken.None);

        Assert.HasCount(39, facade.LastHistory);
        Assert.AreEqual("первая реплика", facade.LastHistory[0].Text);
        var overflow = await chat.SendAsync(id, AC.ChatScope.Knowledge, new AC.ChatMessageRequest("лишняя реплика"), CancellationToken.None);
        Assert.IsNotNull(overflow.Error);
        Assert.HasCount(40, (await chat.GetAsync(id, AC.ChatScope.Knowledge, CancellationToken.None))!.Messages);
    }

    [TestMethod]
    public async Task OversizedConversationFailsExplicitlyWithoutForgettingEarlierMessages()
    {
        var facade = new HistoryFacade(AC.ChatScope.Knowledge);
        var chat = new AC.ChatService(new AC.EphemeralChatSessionStore(), [facade]);
        var first = await chat.SendAsync(null, AC.ChatScope.Knowledge, new AC.ChatMessageRequest(new string('а', 31_900)), CancellationToken.None);

        var overflow = await chat.SendAsync(first.Session!.Id, AC.ChatScope.Knowledge, new AC.ChatMessageRequest(new string('б', 200)), CancellationToken.None);
        Assert.IsNotNull(overflow.Error);
        Assert.HasCount(2, (await chat.GetAsync(first.Session.Id, AC.ChatScope.Knowledge, CancellationToken.None))!.Messages);
    }

    private sealed class ProbeFacade(AC.ChatScope scope) : AC.IChatCommandFacade
    {
        public AC.ChatScope Scope => scope;
        public int Calls { get; private set; }
        public Task<AC.ChatReply> HandleAsync(string text, bool initialPrompt, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new AC.ChatReply("ok", false)); }
    }

    private sealed class MemoryChatStore : AC.IChatSessionStore
    {
        private readonly Dictionary<Guid, AC.ChatSession> _sessions = [];
        public Task<AC.ChatSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<AC.ChatSession> CreateAsync(AC.ChatScope scope, string? initialText, CancellationToken cancellationToken)
        { var now = DateTimeOffset.UtcNow; var messages = string.IsNullOrWhiteSpace(initialText) ? [] : new List<AC.ChatMessage> { new(Guid.NewGuid(), "user", initialText.Trim(), now) }; var session = new AC.ChatSession(Guid.NewGuid(), scope, messages, now, now); _sessions[session.Id] = session; return Task.FromResult(session); }
        public Task<AC.ChatSession?> AppendAsync(Guid id, params AC.ChatMessage[] messages)
        { if (!_sessions.TryGetValue(id, out var current)) return Task.FromResult<AC.ChatSession?>(null); var updated = current with { Messages = [.. current.Messages, .. messages], UpdatedAt = DateTimeOffset.UtcNow }; _sessions[id] = updated; return Task.FromResult<AC.ChatSession?>(updated); }
        public Task<AC.ChatSession?> SetPendingAsync(Guid id, AC.ChatPending? pending, CancellationToken cancellationToken)
        { if (!_sessions.TryGetValue(id, out var current)) return Task.FromResult<AC.ChatSession?>(null); var updated = current with { Pending = pending, UpdatedAt = DateTimeOffset.UtcNow }; _sessions[id] = updated; return Task.FromResult<AC.ChatSession?>(updated); }
    }

    private sealed class FixedResponder : AC.IChatResponder
    { public Task<string> ReplyAsync(string text, IReadOnlyList<AC.ChatMessage>? history, CancellationToken cancellationToken) => Task.FromResult("обычный ответ"); }

    private sealed class CountingResponder : AC.IChatResponder
    {
        public int Calls { get; private set; }
        public Task<string> ReplyAsync(string text, IReadOnlyList<AC.ChatMessage>? history, CancellationToken cancellationToken) { Calls++; return Task.FromResult("ответ модели"); }
    }

    private sealed class ReviewRouterResponder(string decision, double confidence) : AC.IChatResponder, IKnowledgeIntentRouter
    {
        public string LastPreview { get; private set; } = "";
        public Task<string> ReplyAsync(string text, IReadOnlyList<AC.ChatMessage>? history, CancellationToken cancellationToken) => Task.FromResult("ответ модели");
        public Task<(string Status, KnowledgeIntent? Intent)> ClassifyAsync(string text, IReadOnlyList<AC.ChatMessage>? history, CancellationToken cancellationToken) => Task.FromResult<(string, KnowledgeIntent?)>(("unavailable", null));
        public Task<(string Status, string Decision, double Confidence)> ClassifyConfirmationAsync(string preview, string answer, CancellationToken cancellationToken)
        { LastPreview = preview; return Task.FromResult<(string, string, double)>(("ok", decision, confidence)); }
    }

    private sealed class HistoryFacade(AC.ChatScope scope) : AC.IChatConversationFacade
    {
        public AC.ChatScope Scope => scope;
        public IReadOnlyList<AC.ChatMessage> LastHistory { get; private set; } = [];
        public Task<AC.ChatReply> HandleAsync(string text, bool initialPrompt, CancellationToken cancellationToken) => Task.FromResult(new AC.ChatReply("ok", false));
        public Task<AC.ChatReply> HandleAsync(AC.ChatTurn turn, CancellationToken cancellationToken) { LastHistory = turn.History; return Task.FromResult(new AC.ChatReply("ok", false)); }
    }

    private sealed class InMemoryKnowledgeStore(KnowledgeBase.Api.Domain.KnowledgeDocument value) : KnowledgeBase.Api.Application.IKnowledgeStore
    {
        public KnowledgeBase.Api.Domain.KnowledgeDocument Value { get; private set; } = value;
        public int Writes { get; private set; }
        public Task<KnowledgeBase.Api.Domain.KnowledgeDocument> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task WriteAsync(KnowledgeBase.Api.Domain.KnowledgeDocument document, CancellationToken cancellationToken) { Value = document; Writes++; return Task.CompletedTask; }
    }
}
