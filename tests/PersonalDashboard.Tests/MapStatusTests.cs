// Mapping of the gateway /v1/status contract: worker/database/hindsight/queue
// states plus requiresAttention flags.

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class MapStatusTests
{
    [TestMethod]
    public void MapsHealthyStatusJson()
    {
        var status = MemoryRepository.MapStatus(TestJson.Parse(GatewaySample.StatusJson));
        var root = TestJson.RoundTrip(status);

        Assert.AreEqual("healthy", root.GetProperty("database").GetProperty("status").GetString());
        Assert.AreEqual(false, root.GetProperty("database").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual("healthy", root.GetProperty("hindsight").GetProperty("status").GetString());
        Assert.AreEqual("idle", root.GetProperty("worker").GetProperty("status").GetString());
        Assert.AreEqual(false, root.GetProperty("worker").GetProperty("hasLastError").GetBoolean());
        Assert.AreEqual(1, root.GetProperty("queue").GetProperty("queued").GetInt32());
        Assert.AreEqual(42, root.GetProperty("queue").GetProperty("completed").GetInt32());
        Assert.AreEqual(1, root.GetProperty("jobs").EnumerateArray().Count());
        Assert.AreEqual("done", root.GetProperty("jobs").EnumerateArray().First().GetProperty("status").GetString());
        Assert.AreEqual("DESKTOP", root.GetProperty("computers").EnumerateArray().First().GetProperty("name").GetString());
    }

    [TestMethod]
    public void FlagsAttentionOnFailedWorker()
    {
        var status = MemoryRepository.MapStatus(TestJson.Parse(
            "{\"database\":\"degraded\",\"hindsight\":\"unknown\"," +
            "\"worker\":{\"state\":\"failed\",\"last_error\":\"boom\"},\"queue\":{}}"));
        var root = TestJson.RoundTrip(status);
        Assert.AreEqual(true, root.GetProperty("database").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual(true, root.GetProperty("hindsight").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual(true, root.GetProperty("worker").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual(true, root.GetProperty("worker").GetProperty("hasLastError").GetBoolean());
    }

    [TestMethod]
    public void NullRootDegradesToOfflineStatus()
    {
        var status = MemoryRepository.MapStatus(null);
        var root = TestJson.RoundTrip(status);
        Assert.AreEqual("unavailable", root.GetProperty("database").GetProperty("status").GetString());
        Assert.AreEqual(true, root.GetProperty("database").GetProperty("requiresAttention").GetBoolean());
        Assert.AreEqual("unavailable", root.GetProperty("worker").GetProperty("status").GetString());
        Assert.AreEqual(0, root.GetProperty("queue").GetProperty("queued").GetInt32());
        Assert.AreEqual(0, root.GetProperty("jobs").EnumerateArray().Count());
    }
}