using System.Text.Json;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class SingleStepTaskVersionTests
{
    [TestMethod]
    public async Task EditToggleAndNewEditPersistOnlyOneAlternateVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"task-version-{Guid.NewGuid()}.json");
        try
        {
            var item = NewTask();
            var store = new TaskStore(path);
            await store.AddAsync(item);
            Assert.IsNull((await store.GetAsync(item.Id))!.PreviousVersion);
            Assert.IsNull((await store.ToggleVersionAsync(item.Id)).Item);

            await store.UpdateAsync(item.Id, task => task with { Title = "Second", Description = "Second body", Section = "Second section" });
            var saved = (await new TaskStore(path).GetAsync(item.Id))!;
            Assert.AreEqual("First", saved.PreviousVersion?.Title);
            Assert.AreEqual("First body", saved.PreviousVersion?.Description);
            Assert.AreEqual("First section", saved.PreviousVersion?.Section);
            Assert.IsFalse(saved.ShowingAlternate);

            var undone = (await new TaskStore(path).ToggleVersionAsync(item.Id)).Item!;
            Assert.AreEqual("First", undone.Title);
            Assert.AreEqual("Second", undone.PreviousVersion?.Title);
            Assert.IsTrue(undone.ShowingAlternate);

            var redone = (await new TaskStore(path).ToggleVersionAsync(item.Id)).Item!;
            Assert.AreEqual("Second", redone.Title);
            Assert.IsFalse(redone.ShowingAlternate);

            await store.ToggleVersionAsync(item.Id);
            await store.UpdateAsync(item.Id, task => task with { Description = "Third body" });
            saved = (await new TaskStore(path).GetAsync(item.Id))!;
            Assert.AreEqual("First", saved.PreviousVersion?.Title);
            Assert.AreEqual("First body", saved.PreviousVersion?.Description);
            Assert.AreEqual("Third body", saved.Description);
            Assert.IsFalse(saved.ShowingAlternate);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task BucketAndNoOpUpdatesKeepContentHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"task-version-{Guid.NewGuid()}.json");
        try
        {
            var item = NewTask();
            var store = new TaskStore(path);
            await store.AddAsync(item);
            await store.UpdateAsync(item.Id, task => task with { Title = "Second" });
            await store.UpdateAsync(item.Id, task => task with { Bucket = TaskBucket.Today, Status = TaskBoard.Domain.TaskStatus.InProgress });
            await store.UpdateAsync(item.Id, task => task with { Title = task.Title });
            var saved = (await store.GetAsync(item.Id))!;
            Assert.AreEqual("First", saved.PreviousVersion?.Title);
            Assert.AreEqual(TaskBucket.Today, saved.Bucket);
            Assert.AreEqual("First", (await store.ToggleVersionAsync(item.Id)).Item?.Title);
            Assert.AreEqual(TaskBucket.Today, (await store.GetAsync(item.Id))?.Bucket);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task OlderJsonWithoutVersionFieldsLoadsAndChatBatchRecordsEdit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"task-version-{Guid.NewGuid()}.json");
        try
        {
            var item = NewTask();
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { item }));
            var store = new TaskStore(path);
            var loaded = (await store.GetAsync(item.Id))!;
            Assert.IsNull(loaded.PreviousVersion);
            Assert.IsFalse(loaded.ShowingAlternate);
            var batch = await store.ApplyBatchIfCurrentAsync([
                new(item.Id, item.Title, item.Description, item.Section, "From chat", item.Description, item.Section)
            ]);
            Assert.IsTrue(batch.Applied);
            Assert.AreEqual("First", (await store.GetAsync(item.Id))?.PreviousVersion?.Title);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ChainedSectionRenamesUseOriginalMembershipForHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"task-version-{Guid.NewGuid()}.json");
        try
        {
            var first = NewTask() with { Section = "A" };
            var second = NewTask() with { Section = "B" };
            var store = new TaskStore(path);
            await store.AddAsync(first);
            await store.AddAsync(second);

            var result = await store.ApplySectionRenamesIfMembersAsync([
                new TaskSectionRename("A", "B", [first.Id]),
                new TaskSectionRename("B", "C", [second.Id])
            ]);

            Assert.IsTrue(result.Applied, result.Error);
            Assert.AreEqual("B", (await store.GetAsync(first.Id))?.Section);
            Assert.AreEqual("A", (await store.GetAsync(first.Id))?.PreviousVersion?.Section);
            Assert.AreEqual("C", (await store.GetAsync(second.Id))?.Section);
            Assert.AreEqual("B", (await store.GetAsync(second.Id))?.PreviousVersion?.Section);
        }
        finally { File.Delete(path); }
    }

    private static TaskItem NewTask() => new(Guid.NewGuid(), "First", "First body", "First section", TaskBucket.Backlog, TaskBoard.Domain.TaskStatus.New, DateTimeOffset.UtcNow);
}
