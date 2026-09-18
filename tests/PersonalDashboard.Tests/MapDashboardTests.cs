// Mapping of the gateway JSON wire contract onto dashboard records
// (lessons / problems / skills / metrics / project-id -> name).
// Uses the package-visible static mapper directly — no network involved.

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class MapDashboardTests
{
    [TestMethod]
    public void MapsSampleGatewayJsonToDashboardPayload()
    {
        var payload = MemoryRepository.MapDashboard(TestJson.Parse(GatewaySample.DashboardJson));

        // ----- lessons -----
        Assert.AreEqual(1, payload.Lessons.Count);
        var lesson = payload.Lessons[0];
        Assert.AreEqual("Пересборка кэша", lesson.Title);
        Assert.AreEqual("cache-rebuild", lesson.Method);
        Assert.AreEqual("Пересобрать бандл", lesson.WorkingMethod);
        Assert.AreEqual("Спрайт не попадает в бандл", lesson.Problem);
        Assert.AreEqual("Кэш устарел", lesson.Cause);
        Assert.AreEqual("Android bundle", lesson.Conditions);
        Assert.AreEqual("Lost Cyber Hamster", lesson.Project); // project_id -> name
        Assert.AreEqual("project", lesson.Scope);
        Assert.AreEqual("active", lesson.Status);
        Assert.AreEqual("codex", lesson.Agent);
        Assert.AreEqual(4, lesson.AppliedCount);
        Assert.AreEqual(2, lesson.VerifiedCount);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-15T09:00:00Z"), lesson.OccurredAt);
        Assert.IsTrue(lesson.Id != 0, "lesson id hashed from string id");
        Assert.IsNotNull(lesson.LastReadAt);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-15T09:00:00Z"), lesson.LastReadAt);
        Assert.AreEqual(2, lesson.ReadCount);
        Assert.IsNotNull(lesson.LastAppliedAt);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-16T09:00:00Z"), lesson.LastAppliedAt);

        // ----- problems -----
        Assert.AreEqual(1, payload.Problems.Count);
        var problem = payload.Problems[0];
        Assert.AreEqual("Спрайт пропал", problem.Title);
        Assert.AreEqual("Не попадает в бандл", problem.Summary);
        Assert.AreEqual("Lost Cyber Hamster", problem.Project);
        Assert.AreEqual(1, problem.SolutionCount);
        Assert.AreEqual(4, problem.AppliedCount);
        Assert.AreEqual(2, problem.VerifiedCount);

        // The gateway's per-problem lessons[] maps onto problem.Solutions.
        Assert.AreEqual(1, problem.Solutions.Count);
        var solution = problem.Solutions[0];
        Assert.AreEqual("Пересборка кэша", solution.Title);
        Assert.AreEqual(4, solution.AppliedCount);
        Assert.AreEqual(2, solution.VerifiedCount);

        // ----- skills -----
        Assert.AreEqual(1, payload.Skills.Count);
        var skill = payload.Skills[0];
        Assert.AreEqual("graphify-use", skill.Name);
        Assert.AreEqual("Чтение графа", skill.Description);
        Assert.AreEqual("Общий опыт", skill.Project); // "*" -> Общий опыт
        Assert.AreEqual("v3", skill.Version);
        Assert.AreEqual(3, skill.VersionCount);
        Assert.AreEqual(0, skill.Dependencies.Count());

        // ----- skill events -----
        Assert.AreEqual(1, payload.SkillEvents.Count);
        Assert.AreEqual("graphify-use", payload.SkillEvents[0].Name);
        Assert.AreEqual("codex", payload.SkillEvents[0].Agent);

        // ----- metrics (anonymous object -> json round-trip) -----
        var metrics = TestJson.RoundTrip(payload.Metrics);
        Assert.AreEqual(1, metrics.GetProperty("lessonsTotal").GetInt32());
        Assert.AreEqual(4, metrics.GetProperty("appliedTotal").GetInt32());
        Assert.AreEqual(2, metrics.GetProperty("verifiedTotal").GetInt32());
        Assert.AreEqual(1, metrics.GetProperty("problemsTotal").GetInt32());
        Assert.AreEqual(1, metrics.GetProperty("skillsTotal").GetInt32());
        var byProject = metrics.GetProperty("byProject");
        Assert.AreEqual(4, byProject.GetProperty("lost-cyber-hamster-2025").GetProperty("applied").GetInt32());
        Assert.AreEqual(1, metrics.GetProperty("recentPrepares").EnumerateArray().Count());

        var recentRead = metrics.GetProperty("recentReads").EnumerateArray().First();
        Assert.AreEqual("l1", recentRead.GetProperty("id").GetString());
        Assert.AreEqual("Пересборка кэша", recentRead.GetProperty("title").GetString());
        Assert.AreEqual("2026-09-15T09:00:00Z", recentRead.GetProperty("readAt").GetString());
        var recentApply = metrics.GetProperty("recentApplied").EnumerateArray().First();
        Assert.AreEqual("l1", recentApply.GetProperty("id").GetString());
        Assert.AreEqual("Пересборка кэша", recentApply.GetProperty("title").GetString());
        Assert.AreEqual("2026-09-16T09:00:00Z", recentApply.GetProperty("appliedAt").GetString());
        var recentAdd = metrics.GetProperty("recentAdded").EnumerateArray().First();
        Assert.AreEqual("l1", recentAdd.GetProperty("id").GetString());
        Assert.AreEqual("Пересборка кэша", recentAdd.GetProperty("title").GetString());
        Assert.AreEqual("2026-09-15T09:00:00Z", recentAdd.GetProperty("addedAt").GetString());

        // ----- window -----
        Assert.AreEqual("24h", payload.Window);
    }

    [TestMethod]
    public void MapsNullRootToNullPayload()
    {
        Assert.IsNull(MemoryRepository.MapDashboard(null));
    }

    [TestMethod]
    public void ToleratesMissingCollectionsAndFields()
    {
        var payload = MemoryRepository.MapDashboard(TestJson.Parse("{\"metrics\":{}}"));
        Assert.IsNotNull(payload);
        Assert.AreEqual(0, payload.Lessons.Count);
        Assert.AreEqual(0, payload.Problems.Count);
        Assert.AreEqual(0, payload.Skills.Count);
        Assert.AreEqual(0, payload.SkillEvents.Count);
        Assert.AreEqual(0, TestJson.RoundTrip(payload.Metrics).GetProperty("lessonsTotal").GetInt32());
        Assert.AreEqual("", payload.Window);

        // Old-gateway contract: absent optional lesson fields -> null/0, absent
        // recent-activity feeds -> empty arrays (never a crash).
        var bareLesson = MemoryRepository.MapDashboard(TestJson.Parse("{\"lessons\":[{}]}"));
        Assert.IsNull(bareLesson.Lessons[0].LastReadAt);
        Assert.AreEqual(0, bareLesson.Lessons[0].ReadCount);
        Assert.IsNull(bareLesson.Lessons[0].LastAppliedAt);
        var tolerantMetrics = TestJson.RoundTrip(payload.Metrics);
        Assert.AreEqual(0, tolerantMetrics.GetProperty("recentReads").EnumerateArray().Count());
        Assert.AreEqual(0, tolerantMetrics.GetProperty("recentApplied").EnumerateArray().Count());
        Assert.AreEqual(0, tolerantMetrics.GetProperty("recentAdded").EnumerateArray().Count());

        // Old-gateway contract: a problem without lessons -> empty Solutions, never null.
        var bareProblem = MemoryRepository.MapDashboard(TestJson.Parse("{\"problems\":[{}]}"));
        Assert.AreEqual(0, bareProblem.Problems[0].Solutions.Count);
    }
}