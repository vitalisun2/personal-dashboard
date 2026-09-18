// End-to-end tests: a REAL MemoryRepository pointed at a local fake gateway
// (tests/fake_gateway.py serving the sample wire contract; start it and set
// MEMORY_TEST_GATEWAY_URL, e.g. http://127.0.0.1:8123).
// Without the variable the tests are skipped (no-op), so `dotnet test` stays
// green in environments without the sidecar.

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class MemoryRepositoryGatewayTests
{
    private static string? GatewayUrl() => Environment.GetEnvironmentVariable("MEMORY_TEST_GATEWAY_URL");

    private static bool GatewayIsUp() => !string.IsNullOrWhiteSpace(GatewayUrl());

    [TestMethod]
    public async Task GetDashboardMapsFakeGatewayPayload()
    {
        if (!GatewayIsUp()) return;
        var root = TestJson.RoundTrip(await new MemoryRepository(GatewayUrl(), null).GetDashboard());

        Assert.AreEqual(1, TestJson.ArrayCount(root, "lessons"));
        Assert.AreEqual(1, TestJson.ArrayCount(root, "problems"));
        Assert.AreEqual(1, TestJson.ArrayCount(root, "skills"));
        Assert.AreEqual(1, TestJson.ArrayCount(root, "skillEvents"));

        var metrics = root.GetProperty("metrics");
        Assert.AreEqual(1, metrics.GetProperty("lessonsTotal").GetInt32());
        Assert.AreEqual(4, metrics.GetProperty("appliedTotal").GetInt32());
        Assert.AreEqual(2, metrics.GetProperty("verifiedTotal").GetInt32());
        Assert.AreEqual("24h", root.GetProperty("window").GetString());
    }

    [TestMethod]
    public async Task GetProjectsReturnsMappedProjectNames()
    {
        if (!GatewayIsUp()) return;
        var projects = await new MemoryRepository(GatewayUrl(), null).GetProjects();

        // lesson + problem share one project_id -> one mapped name
        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual("Lost Cyber Hamster", projects[0]);
    }

    [TestMethod]
    public async Task SearchEndpointsWorkThroughFakeGateway()
    {
        if (!GatewayIsUp()) return;
        var repo = new MemoryRepository(GatewayUrl(), null);

        var lessons = await repo.SearchLessons("кэш", null, null);
        Assert.AreEqual(1, lessons.Count());
        Assert.AreEqual("Пересборка кэша", lessons.First().Title);
        Assert.AreEqual("Пересобрать бандл", lessons.First().WorkingMethod);

        var problems = await repo.SearchProblems("спрайт", "Lost Cyber Hamster");
        Assert.AreEqual(1, problems.Count());
        Assert.AreEqual("Спрайт пропал", problems.First().Title);

        var skills = await repo.SearchSkills("graphify");
        Assert.AreEqual(1, skills.Count());
        Assert.AreEqual("Общий опыт", skills.First().Project);
    }

    [TestMethod]
    public async Task GetStatusMapsHealthyState()
    {
        if (!GatewayIsUp()) return;
        var root = TestJson.RoundTrip(await new MemoryRepository(GatewayUrl(), null).GetStatus());

        Assert.AreEqual("healthy", root.GetProperty("database").GetProperty("status").GetString());
        Assert.AreEqual("idle", root.GetProperty("worker").GetProperty("status").GetString());
        Assert.AreEqual(42, root.GetProperty("queue").GetProperty("completed").GetInt32());
        Assert.AreEqual("DESKTOP", root.GetProperty("computers").EnumerateArray().First().GetString());
    }
}