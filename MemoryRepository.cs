// Agent memory: wire contracts and the real gateway-backed repository.
// The gateway (agent-memory, 127.0.0.1:8766 on the host) is the single
// source of truth; the repository degrades to empty lists and attention
// statuses when the gateway is unreachable. MockMemoryRepository stays as a spare.

using System.Net.Http;
using System.Text.Json;

interface IMemoryRepository
{
    Task<object> GetDashboard();
    Task<object> GetStatus();
    Task<IReadOnlyList<string>> GetProjects();
    Task<IReadOnlyList<string>> GetAgents();
    Task<IEnumerable<MemoryLesson>> SearchLessons(string? q, string? project, string? agent);
    Task<IEnumerable<MemoryProblem>> SearchProblems(string? q, string? project);
    Task<IEnumerable<MemorySkill>> SearchSkills(string? q);
}

record MemoryLesson(int Id, string Title, string Method, string Problem, string Conditions, string Cause,
    string WorkingMethod, string Evidence, string Verification, string Scope, string ProjectId, string Project,
    string Status, string Agent, string Computer, DateTimeOffset OccurredAt, int AppliedCount, int VerifiedCount);

record MemoryProblem(int Id, string Title, string Summary, string Scope, string ProjectId, string Project,
    int SolutionCount, int AppliedCount, int VerifiedCount, DateTimeOffset LastChange);

record MemorySkill(int Id, string Name, string Description, string Scope, string ProjectId, string Project,
    string Status, string Source, string Computer, string Version, int VersionCount, string[] Dependencies,
    string VerifiedExample, DateTimeOffset RegisteredAt, DateTimeOffset UpdatedAt);

record SkillEvent(string Kind, string Name, string Method, string Verification, string Agent, string Computer, DateTimeOffset OccurredAt, string ProjectId);

// Real repository: proxies the agent-memory gateway REST API and maps its
// snake_case contract onto the dashboard records. Failures never propagate:
// an unreachable gateway yields empty lists and explicit attention statuses.
sealed class MemoryRepository : IMemoryRepository
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly int DashboardTtlSeconds = 15;
    private static readonly int StatusTtlSeconds = 10;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    public record Payload(List<MemoryLesson> Lessons, List<MemoryProblem> Problems, List<MemorySkill> Skills,
        List<SkillEvent> SkillEvents, object Metrics, string Window);
    private Payload? _payload;
    private DateTimeOffset _payloadExpiry;
    private object? _status;
    private DateTimeOffset _statusExpiry;

    public MemoryRepository()
    {
        var configured = Environment.GetEnvironmentVariable("MEMORY_GATEWAY_URL");
        _baseUrl = string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:8766" : configured.TrimEnd('/');
        var key = Environment.GetEnvironmentVariable("MEMORY_API_KEY");
        _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    // Test seam: explicit gateway URL/key instead of environment variables.
    public MemoryRepository(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    // -------- public API ---------

    public async Task<object> GetDashboard()
    {
        var payload = await DashboardAsync();
        return payload is null ? OfflineDashboard() : new
        {
            generatedAt = DateTimeOffset.Now,
            lessons = payload.Lessons.ToArray(),
            problems = payload.Problems.ToArray(),
            skills = payload.Skills.ToArray(),
            skillEvents = payload.SkillEvents.ToArray(),
            metrics = payload.Metrics,
            window = payload.Window
        };
    }

    public async Task<object> GetStatus()
    {
        if (_status is null || DateTimeOffset.Now >= _statusExpiry)
        {
            var body = await FetchAsync("/v1/status");
            if (body is not null)
            {
                _status = MapStatus(Parse(body));
                _statusExpiry = DateTimeOffset.Now.AddSeconds(StatusTtlSeconds);
            }
        }
        return _status is not null ? _status : OfflineStatus();
    }

    public async Task<IReadOnlyList<string>> GetProjects()
    {
        var payload = await DashboardAsync();
        if (payload is null) return [];
        return payload.Lessons.Select(x => x.Project)
            .Concat(payload.Problems.Select(x => x.Project))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAgents()
    {
        var payload = await DashboardAsync();
        if (payload is null) return [];
        return payload.Lessons.Select(x => x.Agent)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();
    }

    public async Task<IEnumerable<MemoryLesson>> SearchLessons(string? q, string? project, string? agent)
    {
        var payload = await DashboardAsync();
        if (payload is null) return [];
        var query = payload.Lessons.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(project))
            query = query.Where(x => x.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(agent))
            query = query.Where(x => x.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(x => words.Any(w => SearchText(x).Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        return query.OrderByDescending(x => x.OccurredAt);
    }

    public async Task<IEnumerable<MemoryProblem>> SearchProblems(string? q, string? project)
    {
        var payload = await DashboardAsync();
        if (payload is null) return [];
        var query = payload.Problems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(project))
            query = query.Where(x => x.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => (x.Title + " " + x.Summary).Contains(q, StringComparison.OrdinalIgnoreCase));
        return query.OrderByDescending(x => x.LastChange);
    }

    public async Task<IEnumerable<MemorySkill>> SearchSkills(string? q)
    {
        var payload = await DashboardAsync();
        if (payload is null) return [];
        var query = payload.Skills.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => (x.Name + " " + x.Description + " " + x.Project).Contains(q, StringComparison.OrdinalIgnoreCase));
        return query.OrderBy(x => x.Status == "active" ? 0 : 1).ThenBy(x => x.Name);
    }

    // -------- gateway access ---------

    private async Task<string?> FetchAsync(string path)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            if (_apiKey is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task<Payload?> DashboardAsync()
    {
        if (_payload is null || DateTimeOffset.Now >= _payloadExpiry)
        {
            var body = await FetchAsync("/v1/dashboard");
            if (body is not null)
            {
                _payload = MapDashboard(Parse(body));
                _payloadExpiry = DateTimeOffset.Now.AddSeconds(DashboardTtlSeconds);
            }
        }
        return _payload;
    }

    private static JsonElement? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    // -------- mapping ---------

    public static Payload MapDashboard(JsonElement? root)
    {
        if (root is null) return null;
        var metrics = Node(root, "metrics");
        var prepares = new List<object>();
        var prepareNodes = ArrayNodes(metrics, "recent_prepares");
        foreach (var p in prepareNodes)
            {
                prepares.Add(new
                {
                    agent = Str(Prop(p, "agent")),
                    computer = Str(Prop(p, "computer")),
                    occurredAt = Str(Prop(p, "occurred_at")),
                    projectId = Str(Prop(p, "project_id")),
                    task = Str(Prop(p, "task")),
                    foundRecords = MaybeInt(Prop(p, "found_records")),
                    warning = NullableStr(Prop(p, "warning"))
                });
            }
        var metricObject = new
        {
            lessonsTotal = Int(Prop(metrics, "lessons_total")),
            appliedTotal = Int(Prop(metrics, "applied_total")),
            verifiedTotal = Int(Prop(metrics, "verified_total")),
            problemsTotal = Int(Prop(metrics, "problems_total")),
            skillsTotal = Int(Prop(metrics, "skills_total")),
            byAgent = MapCounters(Prop(metrics, "by_agent")),
            byProject = MapCounters(Prop(metrics, "by_project")),
            recentPrepares = prepares.ToArray(),
            note = Str(Prop(metrics, "note"))
        };
        var lessons = new List<MemoryLesson>();
        foreach (var node in ArrayNodes(root, "lessons"))
            lessons.Add(MapLesson(node));
        var problems = new List<MemoryProblem>();
        foreach (var node in ArrayNodes(root, "problems"))
            problems.Add(MapProblem(node));
        var skills = new List<MemorySkill>();
        foreach (var node in ArrayNodes(root, "skills"))
            skills.Add(MapSkill(node));
        var events = new List<SkillEvent>();
        foreach (var node in ArrayNodes(root, "skill_events"))
            events.Add(MapSkillEvent(node));
        return new Payload(lessons, problems, skills, events, metricObject, Str(Prop(root, "window")));
    }

    private static Dictionary<string, object> MapCounters(JsonElement? group)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in (group?.EnumerateObject() ?? []))
        {
            result[p.Name] = new
            {
                lessons = Int(Prop(p.Value, "lessons")),
                applied = Int(Prop(p.Value, "applied")),
                verified = Int(Prop(p.Value, "verified"))
            };
        }
        return result;
    }

    private static MemoryLesson MapLesson(JsonElement e) => new(
        HashId(Str(Prop(e, "id"))),
        Str(Prop(e, "title")),
        Str(Prop(e, "method")),
        Str(Prop(e, "problem")),
        Str(Prop(e, "conditions")),
        Str(Prop(e, "cause")),
        Str(Prop(e, "working_method")),
        Str(Prop(e, "evidence")),
        Str(Prop(e, "verification")),
        Str(Prop(e, "scope")),
        Str(Prop(e, "project_id")),
        ProjectName(Str(Prop(e, "project_id"))),
        Str(Prop(e, "status")),
        Str(Prop(e, "agent")),
        Str(Prop(e, "computer")),
        Iso(Prop(e, "occurred_at")) ?? Epoch(),
        Int(Prop(e, "applied_count")),
        Int(Prop(e, "verified_count")));

    private static MemoryProblem MapProblem(JsonElement e) => new(
        HashId(Str(Prop(e, "id"))),
        Str(Prop(e, "title")),
        Str(Prop(e, "summary")),
        Str(Prop(e, "scope")),
        Str(Prop(e, "project_id")),
        ProjectName(Str(Prop(e, "project_id"))),
        Int(Prop(e, "solution_count")),
        Int(Prop(e, "applied_count")),
        Int(Prop(e, "verified_count")),
        Iso(Prop(e, "last_change")) ?? Epoch());

    private static MemorySkill MapSkill(JsonElement e)
    {
        var dependencies = new List<string>();
        var dependencyNodes = ArrayNodes(e, "dependencies");
        foreach (var d in dependencyNodes)
            dependencies.Add(Str(d));
        return new MemorySkill(
            HashId(Str(Prop(e, "id"))),
            Str(Prop(e, "name")),
            Str(Prop(e, "description")),
            Str(Prop(e, "scope")),
            Str(Prop(e, "project_id")),
            ProjectName(Str(Prop(e, "project_id"))),
            Str(Prop(e, "status")),
            Str(Prop(e, "source")),
            Str(Prop(e, "computer")),
            Str(Prop(e, "version")),
            Int(Prop(e, "version_count")),
            dependencies.ToArray(),
            Str(Prop(e, "verified_example")),
            Iso(Prop(e, "registered_at")) ?? Epoch(),
            Iso(Prop(e, "updated_at")) ?? Epoch());
    }

    private static SkillEvent MapSkillEvent(JsonElement e)
    {
        var skillName = Str(Prop(e, "skill"));
        return new SkillEvent(
            Str(Prop(e, "kind")),
            string.IsNullOrEmpty(skillName) ? Str(Prop(e, "method")) : skillName,
            Str(Prop(e, "method")),
            Str(Prop(e, "verification")),
            Str(Prop(e, "agent")),
            Str(Prop(e, "computer")),
            Iso(Prop(e, "occurred_at")) ?? Epoch(),
            Str(Prop(e, "project_id")));
    }

    public static object MapStatus(JsonElement? root)
    {
        if (root is null) return OfflineStatus();
        var worker = Node(root, "worker");
        var workerState = Str(Prop(worker, "state"));
        var workerError = Str(Prop(worker, "last_error"));
        var jobs = new List<object>();
        foreach (var j in ArrayNodes(root, "jobs"))
        {
            jobs.Add(new
            {
                id = Str(Prop(j, "event_id")),
                status = Str(Prop(j, "status"))
            });
        }
        var computers = new List<object>();
        foreach (var c in ArrayNodes(root, "computers"))
        {
            computers.Add(new
            {
                name = Str(c),
                status = "known"
            });
        }
        var database = Str(Prop(root, "database"));
        var hindsight = Str(Prop(root, "hindsight"));
        var queue = Node(root, "queue");
        return new
        {
            database = new
            {
                status = database,
                requiresAttention = database != "healthy"
            },
            hindsight = new
            {
                status = hindsight,
                requiresAttention = hindsight != "healthy"
            },
            worker = new
            {
                status = workerState,
                requiresAttention = workerError.Length > 0 || workerState == "failed",
                hasLastError = workerError.Length > 0
            },
            queue = new
            {
                queued = Int(Prop(queue, "queued")),
                processing = Int(Prop(queue, "processing")),
                completed = Int(Prop(queue, "completed")),
                failed = Int(Prop(queue, "failed"))
            },
            jobs = jobs.ToArray(),
            computers = computers.ToArray()
        };
    }

    // -------- offline fallbacks ---------

    private static object OfflineDashboard() => new
    {
        generatedAt = DateTimeOffset.Now,
        lessons = new object[] {},
        problems = new object[] {},
        skills = new object[] {},
        skillEvents = new object[] {},
        metrics = new
        {
            lessonsTotal = 0,
            appliedTotal = 0,
            verifiedTotal = 0,
            problemsTotal = 0,
            skillsTotal = 0,
            byAgent = new Dictionary<string, object>(StringComparer.Ordinal),
            byProject = new Dictionary<string, object>(StringComparer.Ordinal),
            recentPrepares = new object[] {},
            note = "Гейтвей памяти недоступен — данные не получены."
        },
        window = "Офлайн"
    };

    private static object OfflineStatus() => new
    {
        database = new { status = "unavailable", requiresAttention = true },
        hindsight = new { status = "unavailable", requiresAttention = true },
        worker = new { status = "unavailable", requiresAttention = true, hasLastError = false },
        queue = new { queued = 0, processing = 0, completed = 0, failed = 0 },
        jobs = new object[] {},
        computers = new object[] {}
    };

    // -------- json/ value helpers ---------

    private static JsonElement? Node(JsonElement? root, string name)
    {
        try { return root?.GetProperty(name); }
        catch { return null; }
    }

    private static List<JsonElement> ArrayNodes(JsonElement? root, string name)
    {
        var items = new List<JsonElement>();
        foreach (var e in (Node(root, name)?.EnumerateArray() ?? []))
            items.Add(e);
        return items;
    }

    private static JsonElement? Prop(JsonElement? node, string name)
    {
        try { return node?.GetProperty(name); }
        catch { return null; }
    }

    private static string Str(JsonElement? value)
    {
        try { return value?.GetString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static string? NullableStr(JsonElement? value)
    {
        try { return value?.GetString()?.Trim(); }
        catch { return null; }
    }

    private static int Int(JsonElement? value)
    {
        try { return value?.GetInt32() ?? 0; }
        catch { return 0; }
    }

    private static int? MaybeInt(JsonElement? value)
    {
        try { return value?.GetInt32(); }
        catch { return null; }
    }

    private static DateTimeOffset? Iso(JsonElement? value)
    {
        try
        {
            var text = value?.GetString();
            return text is null ? null : DateTimeOffset.Parse(text);
        }
        catch { return null; }
    }

    private static DateTimeOffset Epoch() => DateTimeOffset.Parse("1970-01-01T00:00:00Z");

    private static int HashId(string id)
    {
        var hash = 0;
        foreach (var ch in id)
            hash = hash * 31 + ch;
        return hash;
    }

    private static string ProjectName(string id)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lost-cyber-hamster-2025"] = "Lost Cyber Hamster",
            ["agent-memory-system"] = "Общая память агентов",
            ["*"] = "Общий опыт"
        };
        foreach (var (keyName, value) in names)
        {
            if (keyName == id) return value;
        }
        return id;
    }

    private static string SearchText(MemoryLesson x) => string.Join(' ', x.Title, x.Method, x.Problem, x.Conditions, x.Cause, x.WorkingMethod, x.Evidence, x.Verification, x.Project, x.Agent);
}