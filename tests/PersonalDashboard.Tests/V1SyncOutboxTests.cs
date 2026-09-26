using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class V1SyncOutboxTests
{
    [TestMethod]
    public async Task QueueSurvivesRestartAndReusesOperationIdAfterLostResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-sync-outbox-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "outbox.json");
            var disabledPath = Path.Combine(directory, "disabled.json");
            var disabled = CreateOutbox(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SYNC_OUTBOX_FILE"] = disabledPath }).Build(), new SyncHandler());
            await disabled.EnqueueAsync("tasks.task", Guid.NewGuid(), "delete", null, CancellationToken.None);
            Assert.IsFalse(File.Exists(disabledPath), "Empty URL/key disables queue persistence as well as delivery.");

            var handler = new SyncHandler { FailFirstPush = true };
            var config = CreateConfiguration(path);
            var taskId = Guid.NewGuid();
            var payload = JsonSerializer.SerializeToElement(new { operation = "create", kind = "task", id = taskId, title = "Task", description = "Body", placement = "backlog", workStatus = "new" });
            var first = CreateOutbox(config, handler);
            await first.EnqueueAsync("tasks.task", taskId, "upsert", payload, CancellationToken.None);
            var queued = await File.ReadAllTextAsync(path);
            using var saved = JsonDocument.Parse(queued);
            var operationId = saved.RootElement.GetProperty("operations")[0].GetProperty("operationId").GetGuid();

            var restarted = CreateOutbox(config, handler);
            await AssertRequestFails(() => restarted.PushNextAsync(CancellationToken.None));
            Assert.IsTrue(File.Exists(path));
            await restarted.PushNextAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { operationId, operationId }, handler.PushOperationIds.ToArray());
            using var final = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(0, final.RootElement.GetProperty("operations").GetArrayLength());
            Assert.IsTrue(final.RootElement.GetProperty("versions").EnumerateObject().Any());

            var update = JsonSerializer.SerializeToElement(new { operation = "update", kind = "task", id = taskId, title = "Changed", description = "Body", placement = "backlog", workStatus = "new" });
            await restarted.EnqueueAsync("tasks.task", taskId, "upsert", update, CancellationToken.None);
            handler.ConflictNextPush = true;
            Assert.IsFalse(await restarted.PushNextAsync(CancellationToken.None), "A V2 conflict is classified as unresolved and is not discarded.");
            using var conflicted = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, conflicted.RootElement.GetProperty("operations").GetArrayLength());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task DurableHttpOutboxSendsTaskAndKnowledgeCreateUpdateDeleteWithStableIdentityAndAuth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-sync-crud-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new SyncHandler();
            var outbox = CreateOutbox(CreateConfiguration(Path.Combine(directory, "outbox.json")), handler);
            var taskId = Guid.NewGuid();
            var knowledgeId = Guid.NewGuid();
            var taskCreate = JsonSerializer.SerializeToElement(new { operation = "create", kind = "task", id = taskId, title = "Task", description = "Task body", placement = "backlog", workStatus = "new" });
            var knowledgeCreate = JsonSerializer.SerializeToElement(new { operation = "create", kind = "document", id = knowledgeId, title = "Knowledge", parentId = (Guid?)null, markdown = "Knowledge body" });
            await outbox.EnqueueAsync("tasks.task", taskId, "upsert", taskCreate, CancellationToken.None);
            await outbox.EnqueueAsync("knowledge.node", knowledgeId, "upsert", knowledgeCreate, CancellationToken.None);
            await outbox.PushNextAsync(CancellationToken.None);
            await outbox.PushNextAsync(CancellationToken.None);

            await outbox.EnqueueAsync("tasks.task", taskId, "upsert", JsonSerializer.SerializeToElement(new { operation = "update", kind = "task", id = taskId, title = "Task updated", description = "Task body", placement = "backlog", workStatus = "new" }), CancellationToken.None);
            await outbox.EnqueueAsync("knowledge.node", knowledgeId, "upsert", JsonSerializer.SerializeToElement(new { operation = "update", kind = "document", id = knowledgeId, title = "Knowledge updated", parentId = (Guid?)null, markdown = "Knowledge body" }), CancellationToken.None);
            await outbox.PushNextAsync(CancellationToken.None);
            await outbox.PushNextAsync(CancellationToken.None);
            await outbox.EnqueueAsync("tasks.task", taskId, "delete", null, CancellationToken.None);
            await outbox.EnqueueAsync("knowledge.node", knowledgeId, "delete", null, CancellationToken.None);
            await outbox.PushNextAsync(CancellationToken.None);
            await outbox.PushNextAsync(CancellationToken.None);

            Assert.AreEqual(6, handler.Requests.Count);
            CollectionAssert.AreEqual(new[] { "tasks.task", "knowledge.node", "tasks.task", "knowledge.node", "tasks.task", "knowledge.node" }, handler.Requests.Select(item => item.Type).ToArray());
            Assert.AreEqual(6, handler.Requests.Select(item => item.OperationId).Distinct().Count());
            Assert.IsTrue(handler.Requests.All(item => item.SyncKey == "test-key"), "Each real push request carries the configured peer-auth header.");
            CollectionAssert.AreEqual(new[] { "upsert", "upsert", "upsert", "upsert", "delete", "delete" }, handler.Requests.Select(item => item.Kind).ToArray());
            Assert.AreEqual("Task updated", handler.Requests[2].Payload!.Value.GetProperty("title").GetString());
            Assert.AreEqual("Knowledge updated", handler.Requests[3].Payload!.Value.GetProperty("title").GetString());
            using var empty = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "outbox.json")));
            Assert.AreEqual(0, empty.RootElement.GetProperty("operations").GetArrayLength());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task TaskSectionsResolveByNameAndBucketAndBlockTasksUntilReady()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-sync-sections-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "outbox.json");
            var existingBacklogId = Guid.NewGuid();
            var handler = new SyncHandler { Sections = _ => "[]", FailFirstPush = true };
            var outbox = CreateOutbox(CreateConfiguration(path), handler);
            var taskPayload = (Guid id) => JsonSerializer.SerializeToElement(new { operation = "create", kind = "task", id, title = "Task", description = "Body", placement = "backlog", workStatus = "new" });
            var firstBacklogPayload = taskPayload(Guid.NewGuid());
            await outbox.EnqueueTaskAsync(" Shared ", "backlog", firstBacklogPayload.GetProperty("id").GetGuid(), firstBacklogPayload, CancellationToken.None);
            var backlogTaskPayload = taskPayload(Guid.NewGuid());
            var backlogTaskId = backlogTaskPayload.GetProperty("id").GetGuid();
            await outbox.EnqueueTaskAsync("Shared", "backlog", backlogTaskId, backlogTaskPayload, CancellationToken.None);
            var todayTaskPayload = JsonSerializer.SerializeToElement(new { operation = "create", kind = "task", id = Guid.NewGuid(), title = "Today", description = "Body", placement = "today", workStatus = "new" });
            await outbox.EnqueueTaskAsync("Shared", "today", todayTaskPayload.GetProperty("id").GetGuid(), todayTaskPayload, CancellationToken.None);

            using var before = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var operations = before.RootElement.GetProperty("operations").EnumerateArray().ToArray();
            var sectionOperations = operations.Where(item => item.GetProperty("type").GetString() == "tasks.section").ToArray();
            Assert.AreEqual(2, sectionOperations.Length, "The same section name in each bucket needs one stable section operation per location.");
            Assert.AreNotEqual(sectionOperations[0].GetProperty("id").GetGuid(), sectionOperations[1].GetProperty("id").GetGuid());
            var backlogSectionOperationId = sectionOperations.Single(item => item.GetProperty("payload").GetProperty("bucket").GetString() == "backlog").GetProperty("operationId").GetGuid();

            await AssertRequestFails(() => outbox.PushNextAsync(CancellationToken.None));
            using var afterFailedCreate = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual("tasks.section", afterFailedCreate.RootElement.GetProperty("operations")[0].GetProperty("type").GetString());
            Assert.IsTrue(handler.PushedTypes.All(type => type == "tasks.section"), "A task must remain behind its unresolved section operation.");

            handler.Sections = location => location == "backlog"
                ? $$"""[{"id":"{{existingBacklogId}}","name":"Shared","location":"backlog","version":3}]"""
                : "[]";
            handler.FailFirstPush = false;
            await outbox.PushNextAsync(CancellationToken.None); // Finds the exact backlog section and adopts its ID.
            using var afterResolve = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreNotEqual(backlogSectionOperationId, afterResolve.RootElement.GetProperty("operations")[0].GetProperty("operationId").GetGuid());
            Assert.AreEqual(existingBacklogId, afterResolve.RootElement.GetProperty("operations")[0].GetProperty("payload").GetProperty("sectionId").GetGuid());
            await outbox.PushNextAsync(CancellationToken.None); // Pushes the task with the matched section ID.
            Assert.AreEqual("tasks.task", handler.PushedTypes.Last());
            Assert.AreEqual(existingBacklogId, handler.TaskSectionIds.Last());

            // A second enqueue for the same name and location reuses the persisted mapping and does not add another create.
            var anotherBacklog = taskPayload(Guid.NewGuid());
            await outbox.EnqueueTaskAsync("Shared", "backlog", anotherBacklog.GetProperty("id").GetGuid(), anotherBacklog, CancellationToken.None);
            using var afterReuse = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, afterReuse.RootElement.GetProperty("operations").EnumerateArray().Count(item => item.GetProperty("type").GetString() == "tasks.section"));
            await outbox.PushNextAsync(CancellationToken.None); // Remaining Backlog task.
            await outbox.PushNextAsync(CancellationToken.None); // Creates only the missing Today section.
            Assert.AreEqual("today", handler.PushedSectionBuckets.Last());
            Assert.AreEqual("today", handler.SectionLookups.Last());
            await outbox.PushNextAsync(CancellationToken.None); // Resolves current section ID for the original Today task.
            await outbox.PushNextAsync(CancellationToken.None); // Delivers the original Today task.
            var todaySection = handler.CreatedSections.Values.Single(section => section.Location == "today");
            var originalTodayCreate = sectionOperations.Single(item => item.GetProperty("payload").GetProperty("bucket").GetString() == "today")
                .GetProperty("operationId").GetGuid();
            handler.DeleteSection(todaySection.Id);
            handler.Sections = _ => "[]";
            var afterDelete = JsonSerializer.SerializeToElement(new { operation = "create", kind = "task", id = Guid.NewGuid(), title = "Later Today", description = "Body", placement = "today", workStatus = "new" });
            await outbox.EnqueueTaskAsync("Shared", "today", afterDelete.GetProperty("id").GetGuid(), afterDelete, CancellationToken.None);
            using var beforeRecreate = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var requeued = beforeRecreate.RootElement.GetProperty("operations")[0];
            Assert.AreEqual("tasks.task", requeued.GetProperty("type").GetString());
            Assert.IsTrue(await outbox.PushNextAsync(CancellationToken.None)); // Detects the deletion and queues recreation.
            await outbox.PushNextAsync(CancellationToken.None); // Creates the missing section.
            Assert.AreEqual("tasks.section", handler.PushedTypes.Last());
            Assert.AreNotEqual(originalTodayCreate, handler.PushOperationIds.Last(), "A later task event needs its own stable recreation operation after deletion.");
            await outbox.PushNextAsync(CancellationToken.None);
            Assert.AreEqual("tasks.task", handler.PushedTypes.Last());
            Assert.AreEqual(todaySection.Id, handler.TaskSectionIds.Last(), "Recreated section keeps its deterministic entity ID.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IConfiguration CreateConfiguration(string path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["SYNC_OUTBOX_FILE"] = path,
        ["V2_PEER_URL"] = "http://v2.test",
        ["V1_V2_SYNC_KEY"] = "test-key"
    }).Build();

    private static async Task AssertRequestFails(Func<Task<bool>> action)
    {
        try { await action(); }
        catch (HttpRequestException) { return; }
        Assert.Fail("Expected the simulated network failure.");
    }

    private static V1SyncOutbox CreateOutbox(IConfiguration configuration, SyncHandler handler) =>
        new(configuration, new SyncClientFactory(handler), NullLogger<V1SyncOutbox>.Instance);

    private sealed class SyncClientFactory(SyncHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SyncHandler : HttpMessageHandler
    {
        public sealed record CapturedRequest(string Type, Guid OperationId, string Kind, string? SyncKey, JsonElement? Payload);
        public bool FailFirstPush { get; set; }
        public bool ConflictNextPush { get; set; }
        public Func<string, string>? Sections { get; set; }
        public List<Guid> PushOperationIds { get; } = [];
        public List<string> PushedTypes { get; } = [];
        public List<Guid?> TaskSectionIds { get; } = [];
        public List<string> SectionLookups { get; } = [];
        public List<string> PushedSectionBuckets { get; } = [];
        public List<CapturedRequest> Requests { get; } = [];
        public Dictionary<Guid, RemoteSectionStub> CreatedSections { get; } = [];
        public void DeleteSection(Guid id) => CreatedSections.Remove(id);

        public sealed record RemoteSectionStub(Guid Id, string Name, string Location, long Version);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/sync/state")
                return Json(HttpStatusCode.OK, "{\"epoch\":\"test-epoch\"}");
            if (request.RequestUri.AbsolutePath == "/api/v2/tasks/sections")
            {
                var location = request.RequestUri.Query.Split('=', 2).Last();
                SectionLookups.Add(location);
                using var configured = JsonDocument.Parse(Sections?.Invoke(location) ?? "[]");
                var all = configured.RootElement.EnumerateArray().Select(item => new RemoteSectionStub(
                    item.GetProperty("id").GetGuid(), item.GetProperty("name").GetString()!, item.GetProperty("location").GetString()!,
                    item.TryGetProperty("version", out var version) ? version.GetInt64() : 1)).ToList();
                all.AddRange(CreatedSections.Values.Where(item => item.Location == location));
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(all));
            }
            if (request.RequestUri.AbsolutePath == "/api/v2/sync/push")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var operation = body.RootElement.GetProperty("operations")[0];
                var operationId = operation.GetProperty("operationId").GetGuid();
                var type = operation.GetProperty("type").GetString()!;
                Requests.Add(new(type, operationId, operation.GetProperty("kind").GetString()!, request.Headers.GetValues("X-PersonalDashboard-Sync-Key").Single(),
                    operation.TryGetProperty("payload", out var capturedPayload) && capturedPayload.ValueKind == JsonValueKind.Object ? capturedPayload.Clone() : null));
                PushOperationIds.Add(operationId);
                PushedTypes.Add(type);
                if (type == "tasks.task" && operation.GetProperty("payload").ValueKind == JsonValueKind.Object)
                {
                    var payload = operation.GetProperty("payload");
                    TaskSectionIds.Add(payload.TryGetProperty("sectionId", out var sectionId) && sectionId.ValueKind == JsonValueKind.String ? sectionId.GetGuid() : null);
                }
                if (type == "tasks.section")
                    PushedSectionBuckets.Add(operation.GetProperty("payload").GetProperty("bucket").GetString()!);
                if (FailFirstPush)
                {
                    FailFirstPush = false;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                if (ConflictNextPush)
                {
                    ConflictNextPush = false;
                    var conflict = $$"""{"results":[{"operationId":"{{operationId}}","applied":false,"skipped":false,"current":null,"conflictReason":"stale"}]}""";
                    return Json(HttpStatusCode.OK, conflict);
                }
                var id = operation.GetProperty("id").GetGuid();
                if (type == "tasks.section")
                {
                    var section = operation.GetProperty("payload");
                    var sectionId = section.GetProperty("id").GetGuid();
                    CreatedSections[sectionId] = new(sectionId, section.GetProperty("title").GetString()!, section.GetProperty("bucket").GetString()!, 1);
                }
                var payloadJson = type == "tasks.task" ? "null" : "null";
                var result = $$"""{"results":[{"operationId":"{{operationId}}","applied":true,"skipped":false,"current":{"type":"{{type}}","id":"{{id}}","version":1,"deleted":false,"payload":{{payloadJson}}},"conflictReason":null}]}""";
                return Json(HttpStatusCode.OK, result);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
