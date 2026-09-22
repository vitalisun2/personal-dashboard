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

        var renamed = await ChatRoutes.HandleAsync($"Измени заголовок задачи {created.Task.Id} на Проверить архивы", store, new LocalTaskAgent());
        Assert.IsFalse(renamed.NeedsClarification);
        Assert.AreEqual("Проверить архивы", (await store.GetAsync(created.Task.Id))!.Title);

        var forbidden = await ChatRoutes.HandleAsync("Измени статус задачи на завершено", store, new LocalTaskAgent());
        Assert.IsTrue(forbidden.NeedsClarification);
        Assert.AreEqual(1, (await store.GetAllAsync()).Count);
    }
}
