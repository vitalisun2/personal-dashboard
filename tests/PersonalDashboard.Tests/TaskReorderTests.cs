namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskReorderTests
{
    private static TaskItem NewTask(string section) => new(
        Guid.NewGuid(), "Задача", "Описание", section, TaskBucket.Backlog, TaskBoard.Domain.TaskStatus.New, DateTimeOffset.UtcNow);

    private static TaskStore NewStore()
    {
        var directory = Environment.GetEnvironmentVariable("TEMP") ?? ".";
        return new TaskStore(Path.Combine(directory, "tasks-reorder-test-" + Guid.NewGuid() + ".json"));
    }

    [TestMethod]
    public async Task ReordersOnlyTasksInsideRequestedSection()
    {
        var store = NewStore();
        var first = NewTask("Работа");
        var second = NewTask("Работа");
        var other = NewTask("Личное");
        await store.AddAsync(first);
        await store.AddAsync(second);
        await store.AddAsync(other);

        Assert.IsTrue(await store.ReorderTasksAsync(TaskBucket.Backlog, "Работа", [first.Id, second.Id]));

        var items = await store.GetAllAsync();
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, items.Where(x => x.Section == "Работа").Select(x => x.Id).ToArray());
        Assert.AreEqual(other.Id, items.Single(x => x.Section == "Личное").Id);
    }

    [TestMethod]
    public async Task ReordersSectionsWithoutChangingTheirTaskOrder()
    {
        var store = NewStore();
        var firstWork = NewTask("Работа");
        var secondWork = NewTask("Работа");
        var home = NewTask("Личное");
        await store.AddAsync(firstWork);
        await store.AddAsync(secondWork);
        await store.AddAsync(home);
        var beforeWork = (await store.GetAllAsync()).Where(x => x.Section == "Работа").Select(x => x.Id).ToArray();

        Assert.IsTrue(await store.ReorderSectionsAsync(TaskBucket.Backlog, ["Работа", "Личное"]));

        var items = await store.GetAllAsync();
        CollectionAssert.AreEqual(new[] { "Работа", "Работа", "Личное" }, items.Select(x => x.Section).ToArray());
        CollectionAssert.AreEqual(beforeWork, items.Where(x => x.Section == "Работа").Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task RejectsPartialOrderAndLeavesTasksUntouched()
    {
        var store = NewStore();
        var first = NewTask("Работа");
        var second = NewTask("Работа");
        await store.AddAsync(first);
        await store.AddAsync(second);
        var before = (await store.GetAllAsync()).Select(x => x.Id).ToArray();

        Assert.IsFalse(await store.ReorderTasksAsync(TaskBucket.Backlog, "Работа", [first.Id]));

        CollectionAssert.AreEqual(before, (await store.GetAllAsync()).Select(x => x.Id).ToArray());
    }
}
