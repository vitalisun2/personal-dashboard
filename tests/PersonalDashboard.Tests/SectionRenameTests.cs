// TaskStore business logic behind PUT /api/tasks/sections/rename:
// renaming a section rewrites the Section field of every matching task
// (case-insensitive match, names trimmed) and rewrites the JSON file
// atomically only when something actually changed. Uses the public
// test-seam TaskStore(string) constructor with a unique temp file.

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class SectionRenameTests
{
    private static (TaskStore Store, string Path) CreateStore()
    {
        var tempDir = Environment.GetEnvironmentVariable("TEMP")
            ?? Environment.GetEnvironmentVariable("TMP")
            ?? ".";
        var path = Path.Combine(tempDir, "tasks-rename-test-" + Guid.NewGuid() + ".json");
        return (new TaskStore(path), path);
    }

    private static TaskItem NewTask(string section) => new(
        Guid.NewGuid(), "Задача", "Описание", section, TaskBucket.Backlog, TaskBoard.Domain.TaskStatus.New, DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task RenamesEveryTaskInSection()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("Работа"));
        await store.AddAsync(NewTask("Работа"));
        await store.AddAsync(NewTask("Общее"));

        var renamed = await store.RenameSectionAsync("Работа", "Проект");

        Assert.AreEqual(2, renamed);
        var all = await store.GetAllAsync();
        Assert.AreEqual(2, all.Count(x => x.Section == "Проект"));
        Assert.AreEqual(1, all.Count(x => x.Section == "Общее"));
    }

    [TestMethod]
    public async Task MatchesSectionCaseInsensitively()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("Workflow"));

        Assert.AreEqual(1, await store.RenameSectionAsync("workflow", "Канбан"));
        Assert.AreEqual("Канбан", (await store.GetAllAsync())[0].Section);
    }

    [TestMethod]
    public async Task RewritesCaseOnlyChange()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("workflow"));

        Assert.AreEqual(1, await store.RenameSectionAsync("WORKFLOW", "Workflow"));
        Assert.AreEqual("Workflow", (await store.GetAllAsync())[0].Section);
    }

    [TestMethod]
    public async Task UnknownSectionChangesNothing()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("Работа"));

        Assert.AreEqual(0, await store.RenameSectionAsync("Несуществующий", "Новое"));
        Assert.AreEqual("Работа", (await store.GetAllAsync())[0].Section);
    }

    [TestMethod]
    public async Task IdenticalNameChangesNothing()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("Работа"));

        Assert.AreEqual(0, await store.RenameSectionAsync("Работа", "Работа"));
    }

    [TestMethod]
    public async Task TrimsNamesBeforeMatching()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("Работа"));

        Assert.AreEqual(1, await store.RenameSectionAsync("  Работа  ", "  Проект  "));
        Assert.AreEqual("Проект", (await store.GetAllAsync())[0].Section);
    }

    [TestMethod]
    public async Task WhitespaceOnlyNewNameChangesNothing()
    {
        var (store, _) = CreateStore();
        await store.AddAsync(NewTask("Работа"));

        Assert.AreEqual(0, await store.RenameSectionAsync("Работа", "   "));
        Assert.AreEqual("Работа", (await store.GetAllAsync())[0].Section);
    }
}
