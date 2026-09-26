namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskStoreProtectionTests
{
    private static TaskItem NewTask() => new(
        Guid.NewGuid(), "Задача", "Описание", "Общее",
        TaskBucket.Backlog, TaskBoard.Domain.TaskStatus.New, DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task MissingPrimaryRestoresLastBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-protection-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tasks.json");
        try
        {
            var seed = new TaskStore(path);
            var item = NewTask();
            await seed.AddAsync(item);
            File.Copy(path, path + ".bak");
            File.Delete(path);

            var protectedStore = new TaskStore(path, protectionEnabled: true);
            var restored = await protectedStore.GetAllAsync();

            Assert.AreEqual(1, restored.Count);
            Assert.AreEqual(item.Id, restored[0].Id);
            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProtectedStoreDoesNotSilentlyAcceptMissingData()
    {
        var path = Path.Combine(Path.GetTempPath(), "dashboard-protection-" + Guid.NewGuid(), "tasks.json");
        var store = new TaskStore(path, protectionEnabled: true);

        try
        {
            await store.GetAllAsync();
            Assert.Fail("Missing protected task data must not become an empty list.");
        }
        catch (InvalidOperationException)
        {
            // Expected: a production store without a primary file or backup fails loudly.
        }
    }

    [TestMethod]
    public async Task ProtectedWriteKeepsLastStateAndDailySnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-protection-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tasks.json");
        try
        {
            var seed = new TaskStore(path);
            var item = NewTask();
            await seed.AddAsync(item);

            var protectedStore = new TaskStore(path, protectionEnabled: true);
            await protectedStore.UpdateAsync(item.Id, task => task with { Title = "Обновлённая задача" });

            var backup = await new TaskStore(path + ".bak").GetAllAsync();
            Assert.AreEqual("Задача", backup[0].Title);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "archive", DateTime.Now.ToString("yyyy-MM-dd"), "tasks.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PeerImportIsIdempotentAndPreservesV1AlternateState()
    {
        var path = Path.Combine(Path.GetTempPath(), "dashboard-peer-" + Guid.NewGuid(), "tasks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var outbox = new RecordingTaskSyncOutbox();
        var store = new TaskStore(path, outbox);
        var id = Guid.NewGuid();
        var created = NewTask() with { Id = id };
        try
        {
            await store.AddAsync(created);
            await store.UpdateAsync(id, task => task with { Title = "Intermediate" });
            var localOperationCount = outbox.Operations.Count;
            created = (await store.GetAsync(id))!;
            var imported = created with { Title = "Peer title", Description = "Peer body", Section = "Peer section", CreatedAt = DateTimeOffset.UtcNow };
            await store.ImportAsync(imported);
            var first = await store.GetAsync(id);
            var lastWrite = File.GetLastWriteTimeUtc(path);
            await store.ImportAsync(imported);
            var second = await store.GetAsync(id);

            Assert.AreEqual("Peer title", first!.Title);
            Assert.AreEqual(created.CreatedAt, first.CreatedAt);
            Assert.AreEqual(created.PreviousVersion, first.PreviousVersion);
            Assert.AreEqual(created.ShowingAlternate, first.ShowingAlternate);
            Assert.AreEqual(first, second);
            Assert.AreEqual(lastWrite, File.GetLastWriteTimeUtc(path));
            Assert.AreEqual(localOperationCount, outbox.Operations.Count, "Inbound imports must not echo into the outbound queue.");
            await store.DeleteAsync(id, publishSync: false);
            Assert.AreEqual(localOperationCount, outbox.Operations.Count, "Inbound deletes must not echo into the outbound queue.");
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private sealed class RecordingTaskSyncOutbox : ITaskSyncOutbox
    {
        public List<string> Operations { get; } = [];
        public Task EnqueueAsync(string type, Guid id, string operation, System.Text.Json.JsonElement? payload, CancellationToken cancellationToken)
        { Operations.Add($"{type}:{id}:{operation}"); return Task.CompletedTask; }
        public Task EnqueueTaskAsync(string sectionName, string bucket, Guid id, System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
        { Operations.Add($"tasks.task:{id}:upsert:{bucket}:{sectionName}"); return Task.CompletedTask; }
    }
}
