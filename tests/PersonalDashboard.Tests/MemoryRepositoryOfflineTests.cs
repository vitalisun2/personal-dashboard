// Offline path (the core resilience contract): when the memory gateway is
// unreachable, the repository must degrade to empty lists / zero metrics
// (dashboard) and "unavailable" attention statuses — never throw.
//
// Uses a real MemoryRepository pointed at an unreachable loopback port via the
// package-private test constructor (no environment variables involved).

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class MemoryRepositoryOfflineTests
{
    // Port 1 on loopback is always refused; connection fails immediately.
    private static MemoryRepository OfflineRepo() => new MemoryRepository("http://127.0.0.1:1", null);

    [TestMethod]
    public void ReadApiKeyUsesSecretFileWhenEnvironmentValueIsMissing()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "  file-secret\n");
            Assert.AreEqual("file-secret", MemoryRepository.ReadApiKey(null, path));
            Assert.AreEqual("environment-secret", MemoryRepository.ReadApiKey(" environment-secret ", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task GetDashboardDegradesToEmptyContract()
    {
        var root = TestJson.RoundTrip(await OfflineRepo().GetDashboard());

        Assert.AreEqual(0, TestJson.ArrayCount(root, "lessons"));
        Assert.AreEqual(0, TestJson.ArrayCount(root, "problems"));
        Assert.AreEqual(0, TestJson.ArrayCount(root, "skills"));
        Assert.AreEqual(0, TestJson.ArrayCount(root, "skillEvents"));

        var metrics = root.GetProperty("metrics");
        Assert.AreEqual(0, metrics.GetProperty("lessonsTotal").GetInt32());
        Assert.AreEqual(0, metrics.GetProperty("appliedTotal").GetInt32());
        Assert.AreEqual(0, metrics.GetProperty("verifiedTotal").GetInt32());
        Assert.AreEqual(0, metrics.GetProperty("problemsTotal").GetInt32());
        Assert.AreEqual(0, metrics.GetProperty("skillsTotal").GetInt32());
        Assert.AreEqual(0, metrics.GetProperty("byAgent").EnumerateObject().Count());
        Assert.AreEqual(0, metrics.GetProperty("byProject").EnumerateObject().Count());
        Assert.AreEqual(0, metrics.GetProperty("recentPrepares").EnumerateArray().Count());
        Assert.AreEqual("Гейтвей памяти недоступен — данные не получены.", metrics.GetProperty("note").GetString());

        Assert.AreEqual("Офлайн", root.GetProperty("window").GetString());
    }

    [TestMethod]
    public async Task GetStatusDegradesToUnavailable()
    {
        var root = TestJson.RoundTrip(await OfflineRepo().GetStatus());

        Assert.AreEqual("unavailable", root.GetProperty("database").GetProperty("status").GetString());
        Assert.AreEqual(true, root.GetProperty("database").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual("unavailable", root.GetProperty("hindsight").GetProperty("status").GetString());
        Assert.AreEqual("unavailable", root.GetProperty("worker").GetProperty("status").GetString());
        Assert.AreEqual(true, root.GetProperty("worker").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual(false, root.GetProperty("worker").GetProperty("hasLastError").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("queue").GetProperty("queued").GetInt32());
        Assert.AreEqual(0, root.GetProperty("queue").GetProperty("completed").GetInt32());
        Assert.AreEqual(0, root.GetProperty("jobs").EnumerateArray().Count());
        Assert.AreEqual(0, root.GetProperty("computers").EnumerateArray().Count());
    }

    [TestMethod]
    public async Task SearchEndpointsReturnEmptyLists()
    {
        var repo = OfflineRepo();
        Assert.AreEqual(0, (await repo.GetProjects()).Count);
        Assert.AreEqual(0, (await repo.GetAgents()).Count);
        Assert.AreEqual(0, (await repo.SearchLessons("кэш", null, null)).ToList().Count);
        Assert.AreEqual(0, (await repo.SearchProblems("спрайт", null)).ToList().Count);
        Assert.AreEqual(0, (await repo.SearchSkills("graphify")).ToList().Count);
    }
}
