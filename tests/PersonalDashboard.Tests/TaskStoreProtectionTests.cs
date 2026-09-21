namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskStoreProtectionTests
{
    private static TaskItem NewTask() => new(
        Guid.NewGuid(), "Задача", "Описание", "Общее",
        TaskBucket.Backlog, TaskStatus.New, DateTimeOffset.UtcNow);

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
}
