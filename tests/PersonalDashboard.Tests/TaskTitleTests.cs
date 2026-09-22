// TaskStore business logic behind PUT /api/tasks/{id:guid}/title:
// an update replaces the title and trims surrounding whitespace, exactly
// like the route lambda in Program.cs. Uses the public test-seam TaskStore(string)
// constructor with a unique temp file — no server environment, no TASKS_FILE env var.

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskTitleTests
{
    [TestMethod]
    public async Task UpdateTitlePersistsTrimmedValue()
    {
        var tempDir = Environment.GetEnvironmentVariable("TEMP")
            ?? Environment.GetEnvironmentVariable("TMP")
            ?? ".";
        var path = Path.Combine(tempDir, "tasks-title-test-" + Guid.NewGuid() + ".json");
        var store = new TaskStore(path);
        var item = new TaskItem(
            Guid.NewGuid(),
            "Прежний заголовок",
            "Описание задачи",
            "Общее",
            TaskBucket.Backlog,
            TaskBoard.Domain.TaskStatus.New,
            DateTimeOffset.UtcNow);

        await store.AddAsync(item);

        // Роут /title делает ровно это: Trim слева/справа, при null — старый заголовок.
        var updated = await store.UpdateAsync(item.Id, task => task with { Title = "  Новый заголовок  ".Trim() });
        Assert.IsNotNull(updated);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(item.Id, all[0].Id);
        Assert.AreEqual("Новый заголовок", all[0].Title);
        Assert.AreNotEqual("Прежний заголовок", all[0].Title);

        // Null-значение (как request.Title == null в роуте) заголовок не трогает.
        string? empty = null;
        await store.UpdateAsync(item.Id, task => task with { Title = empty?.Trim() ?? task.Title });
        Assert.AreEqual("Новый заголовок", (await store.GetAllAsync())[0].Title);
    }
}
