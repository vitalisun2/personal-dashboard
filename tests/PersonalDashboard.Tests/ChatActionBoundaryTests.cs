using AC = AgentChat;
using BoardStatus = TaskBoard.Domain.TaskStatus;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class ChatActionBoundaryTests
{
    private static TaskStore NewStore() => new(Path.Combine(Path.GetTempPath(), "dashboard-chat-" + Guid.NewGuid() + ".json"));

    [TestMethod]
    public async Task OrdinaryMessageRepliesWithoutMutation()
    {
        var store = NewStore();
        var response = await ChatRoutes.HandleAsync("Расскажи коротко о планировании", store, new LocalTaskAgent());
        Assert.IsFalse(response.NeedsClarification);
        StringAssert.Contains(response.Reply, "Расскажи коротко о планировании");
        Assert.AreEqual(0, (await store.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task CreateTaskUsesGatewayAndStatusRequestHasNoSideEffect()
    {
        var store = NewStore();
        var created = await ChatRoutes.HandleAsync("Создай задачу: проверить резервные копии", store, new LocalTaskAgent());
        Assert.IsNotNull(created.Task);
        Assert.AreEqual(TaskBucket.Backlog, created.Task!.Bucket);

        var unresolved = await ChatRoutes.HandleAsync($"Измени заголовок задачи {created.Task.Id} на Проверить архивы", store, new LocalTaskAgent());
        Assert.IsTrue(unresolved.NeedsClarification);
        Assert.AreEqual(created.Task.Title, (await store.GetAsync(created.Task.Id))!.Title);

        var forbidden = await ChatRoutes.HandleAsync("Измени статус задачи на завершено", store, new LocalTaskAgent());
        Assert.IsTrue(forbidden.NeedsClarification);
        Assert.AreEqual(1, (await store.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task NaturalTaskEditUsesCompleteSnapshotPreviewAndConfirmation()
    {
        var store = NewStore();
        var target = new TaskItem(Guid.NewGuid(), "Подготовить релиз", "Проверить сборку", "Личный дашборд", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow);
        var decoy = new TaskItem(Guid.NewGuid(), "Обновить заметки", "Собрать итоги", "Общее", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow);
        await store.AddAsync(target);
        await store.AddAsync(decoy);
        var agent = new ScopedMutationAgent($"{{\"kind\":\"update_task\",\"taskId\":\"{target.Id}\",\"reference\":\"Подготовить релиз\",\"title\":\"Подготовить релиз\",\"description\":\"Проверить сборку\\nПроверить заметки о декоре\",\"descriptionMode\":\"append\",\"section\":\"Личный дашборд\",\"oldName\":null,\"newName\":null,\"answer\":null,\"question\":null}}");
        var service = new TaskChatService(store, agent);

        var question = await service.HandleAsync("Можно ли переписать описание задачи про подготовку релиза?");
        Assert.IsFalse(question.NeedsClarification);
        Assert.AreEqual("", agent.LastSystemMessage, "Questions should go straight to scoped QA instead of invoking the mutation resolver.");

        var preview = await service.HandleAsync("Дополни описание задачи про подготовку релиза сведениями о декоре", cancellationToken: CancellationToken.None);

        Assert.IsNotNull(preview.PendingData);
        Assert.Contains(target.Id.ToString(), preview.Reply);
        Assert.Contains("Проверить сборку", preview.Reply);
        Assert.Contains("Проверить заметки о декоре", preview.Reply);
        Assert.Contains(decoy.Id.ToString(), agent.LastSystemMessage, "Resolver should receive the full task snapshot, not only the selected task.");
        Assert.AreEqual("Проверить сборку", (await store.GetAsync(target.Id))!.Description);

        var committed = await service.HandleAsync("да", cancellationToken: CancellationToken.None, pendingType: preview.PendingType, pendingData: preview.PendingData);

        Assert.IsFalse(committed.NeedsClarification, committed.Reply);
        Assert.AreEqual("Проверить сборку\nПроверить заметки о декоре", (await store.GetAsync(target.Id))!.Description);

        var oldKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var oldKeyFile = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY_FILE");
        var oldRouter = Environment.GetEnvironmentVariable("OPENROUTER_URL");
        var kbDocument = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Арт - необходимый минимум", Content = "В каждой локации нужен базовый набор декора." };
        var knowledgeStore = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [kbDocument] });
        var kbResponses = new Queue<string>([
            "{\"kind\":\"replace_document\",\"reference\":\"Арт - необходимый минимум\",\"title\":null,\"content\":\"Для каждой локации нужен базовый декор.\",\"section\":null,\"question\":null}",
            "{\"decision\":\"approve\",\"confidence\":0.99}"
        ]);
        string? classifierBody = null;
        var http = new StubHttpHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (classifierBody is null) classifierBody = body;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = kbResponses.Dequeue() } } } }))
            };
        });
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "test-only-key");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY_FILE", null);
        Environment.SetEnvironmentVariable("OPENROUTER_URL", "https://router.test/v1");
        try
        {
            var kbFacade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(knowledgeStore), new ReadOnlyChatResponder(new HttpClient(http)));
            var kbPreview = await kbFacade.HandleAsync(new AC.ChatTurn("Сделай короче описание в документе про арт-минимум.", true, null, []), CancellationToken.None);
            Assert.IsNotNull(kbPreview.Pending);
            Assert.Contains("Полный текст после изменения", kbPreview.Text);
            Assert.Contains("Для каждой локации нужен базовый декор.", kbPreview.Text);
            Assert.IsNotNull(classifierBody);
            using (var sent = System.Text.Json.JsonDocument.Parse(classifierBody))
            {
                var content = string.Join("\n", sent.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString()));
                Assert.Contains("Арт - необходимый минимум", content);
                Assert.Contains("В каждой локации нужен базовый набор декора.", content);
            }
            Assert.AreEqual(0, knowledgeStore.Writes);
            var kbCommitted = await kbFacade.HandleAsync(new AC.ChatTurn("да", false, kbPreview.Pending, []), CancellationToken.None);
            Assert.IsTrue(kbCommitted.ChangedData, kbCommitted.Text);
            Assert.AreEqual("Для каждой локации нужен базовый декор.", kbDocument.Content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", oldKey);
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY_FILE", oldKeyFile);
            Environment.SetEnvironmentVariable("OPENROUTER_URL", oldRouter);
            http.Dispose();
        }
    }

    [TestMethod]
    public async Task MoveAndLegacyDeletePendingAreRejectedWithoutWrites()
    {
        var store = NewStore();
        var task = new TaskItem(Guid.NewGuid(), "Подготовить релиз", "Проверить сборку", "Личный дашборд", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow);
        await store.AddAsync(task);
        var taskService = new TaskChatService(store, new LocalTaskAgent());
        var move = await taskService.HandleAsync($"Перенеси задачу {task.Id} в раздел Общее");
        Assert.IsTrue(move.NeedsClarification);
        Assert.AreEqual("Личный дашборд", (await store.GetAsync(task.Id))!.Section);

        var staleAgent = new ScopedMutationAgent($"{{\"kind\":\"update_task\",\"taskId\":\"{task.Id}\",\"reference\":\"Подготовить релиз\",\"title\":\"Подготовить релиз\",\"description\":\"Проверить сборку и скриншоты\",\"descriptionMode\":\"append\",\"section\":\"Личный дашборд\"}}");
        var staleService = new TaskChatService(store, staleAgent);
        var stalePreview = await staleService.HandleAsync("Добавь к описанию задачи про релиз проверку скриншотов");
        await store.UpdateAsync(task.Id, item => item with { Description = "Изменено вручную" });
        var staleCommit = await staleService.HandleAsync("да", pendingType: stalePreview.PendingType, pendingData: stalePreview.PendingData);
        Assert.IsTrue(staleCommit.NeedsClarification);
        Assert.AreEqual("Изменено вручную", (await store.GetAsync(task.Id))!.Description);

        var renameAgent = new ScopedMutationAgent("{\"kind\":\"rename_section\",\"oldName\":\"Личный дашборд\",\"newName\":\"Работа\"}");
        var renameService = new TaskChatService(store, renameAgent);
        var renamePreview = await renameService.HandleAsync("Переименуй раздел Личный дашборд в Работа");
        Assert.IsNotNull(renamePreview.PendingData);
        await store.AddAsync(new TaskItem(Guid.NewGuid(), "Новая задача раздела", "Описание", "Личный дашборд", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow));
        var renameCommit = await renameService.HandleAsync("да", pendingType: renamePreview.PendingType, pendingData: renamePreview.PendingData);
        Assert.IsTrue(renameCommit.NeedsClarification);
        Assert.IsTrue((await store.GetAllAsync()).Where(item => item.Section == "Личный дашборд").Count() == 2);
        Assert.IsFalse((await store.GetAllAsync()).Any(item => item.Section == "Работа"));

        await store.AddAsync(new TaskItem(Guid.NewGuid(), "Задача другого раздела", "Описание", "Другое", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow));
        var collisionAgent = new ScopedMutationAgent("{\"kind\":\"rename_section\",\"oldName\":\"Личный дашборд\",\"newName\":\"Другое\"}");
        var collisionService = new TaskChatService(store, collisionAgent);
        var collision = await collisionService.HandleAsync("Переименуй раздел Личный дашборд в Другое");
        Assert.IsTrue(collision.NeedsClarification);
        Assert.IsNull(collision.PendingData);
        Assert.IsFalse((await store.GetAllAsync()).Any(item => item.Section == "Другое" && item.Title == "Подготовить релиз"));

        var collisionStore = NewStore();
        var sourceTask = new TaskItem(Guid.NewGuid(), "Задача источника", "Описание", "Источник", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow);
        await collisionStore.AddAsync(sourceTask);
        var delayedCollisionAgent = new ScopedMutationAgent("{\"kind\":\"rename_section\",\"oldName\":\"Источник\",\"newName\":\"Цель\"}");
        var delayedCollisionService = new TaskChatService(collisionStore, delayedCollisionAgent);
        var delayedCollisionPreview = await delayedCollisionService.HandleAsync("Переименуй раздел Источник в Цель");
        await collisionStore.AddAsync(new TaskItem(Guid.NewGuid(), "Задача цели", "Описание", "Цель", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow));
        var delayedCollisionCommit = await delayedCollisionService.HandleAsync("да", pendingType: delayedCollisionPreview.PendingType, pendingData: delayedCollisionPreview.PendingData);
        Assert.IsTrue(delayedCollisionCommit.NeedsClarification);
        Assert.AreEqual("Источник", (await collisionStore.GetAsync(sourceTask.Id))!.Section);
        Assert.AreEqual(1, (await collisionStore.GetAllAsync()).Count(item => item.Section == "Цель"));

        var emptyRenamePending = "{\"Kind\":\"rename_section\",\"OldName\":\"Несуществующий\",\"NewName\":\"Работа\",\"ExpectedTaskIds\":[],\"Preview\":\"Переименование\"}";
        var emptyRename = await taskService.HandleAsync("да", pendingType: "tasks-chat", pendingData: emptyRenamePending);
        Assert.IsTrue(emptyRename.NeedsClarification);
        Assert.IsNull(emptyRename.Action);

        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Справка", Content = "Оставить" };
        var knowledgeStore = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var facade = new KnowledgeChatFacade(new KnowledgeBase.Api.Application.KnowledgeService(knowledgeStore), new LocalResponder());
        var pending = new AC.ChatPending("knowledge-command", System.Text.Json.JsonSerializer.Serialize(new KnowledgeCommand(KnowledgeCommandKind.DeleteByReference, document.Id, Missing: "approval", Preview: "удалить Справка")));
        var result = await facade.HandleAsync(new AC.ChatTurn("да", false, pending, []), CancellationToken.None);

        Assert.IsFalse(result.ChangedData);
        Assert.AreEqual(0, knowledgeStore.Writes);
        Assert.AreEqual("Оставить", document.Content);
    }

    [TestMethod]
    public async Task KnowledgeContentCompareAndUpdateRejectsStalePreview()
    {
        var document = new KnowledgeBase.Api.Domain.KnowledgeNode { Kind = "document", Title = "Памятка", Content = "Исходный текст" };
        var store = new InMemoryKnowledgeStore(new KnowledgeBase.Api.Domain.KnowledgeDocument { Nodes = [document] });
        var service = new KnowledgeBase.Api.Application.KnowledgeService(store);
        document.Content = "Свежая правка из другого окна";

        var error = await service.UpdateContentIfCurrentAsync(document.Id, "Памятка", "Исходный текст", "Перезаписанный текст", CancellationToken.None);

        StringAssert.Contains(error!, "изменилось после предпросмотра");
        Assert.AreEqual(0, store.Writes);
        Assert.AreEqual("Свежая правка из другого окна", document.Content);
    }

    private sealed class ScopedMutationAgent(string result) : ITaskAgent
    {
        public string LastSystemMessage { get; private set; } = "";
        public Task<string?> ResolveChatActionAsync(IReadOnlyList<TaskConversationMessage> context) { LastSystemMessage = context[0].Text; return Task.FromResult<string?>(result); }
        public Task<string> ChatAsync(string text, IReadOnlyList<TaskConversationMessage>? history = null) => Task.FromResult("ответ");
        public Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections) => Task.FromResult(new TaskDraft("Новая задача", rawText, "Общее"));
        public Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> existingSections) => Task.FromResult(draft);
        public Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> existingSections) => Task.FromResult(current);
    }

    private sealed class LocalResponder : AC.IChatResponder
    {
        public Task<string> ReplyAsync(string text, IReadOnlyList<AC.ChatMessage>? history, CancellationToken cancellationToken) => Task.FromResult("ok");
    }

    private sealed class StubHttpHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class InMemoryKnowledgeStore(KnowledgeBase.Api.Domain.KnowledgeDocument value) : KnowledgeBase.Api.Application.IKnowledgeStore
    {
        public KnowledgeBase.Api.Domain.KnowledgeDocument Value { get; private set; } = value;
        public int Writes { get; private set; }
        public Task<KnowledgeBase.Api.Domain.KnowledgeDocument> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task WriteAsync(KnowledgeBase.Api.Domain.KnowledgeDocument document, CancellationToken cancellationToken) { Value = document; Writes++; return Task.CompletedTask; }
    }
}
