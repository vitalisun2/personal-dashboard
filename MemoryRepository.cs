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
    string Status, string Agent, string Computer, DateTimeOffset OccurredAt, int AppliedCount, int VerifiedCount,
    DateTimeOffset? LastReadAt = null, int ReadCount = 0, DateTimeOffset? LastAppliedAt = null);

record MemoryProblem(int Id, string Title, string Summary, string Scope, string ProjectId, string Project,
    int SolutionCount, int AppliedCount, int VerifiedCount, DateTimeOffset LastChange, List<MemoryLesson> Solutions)
{
    // Secondary positional constructor without solutions: MockMemoryRepository and
    // other callers keep compiling; new problems start with an empty (never null)
    // list instead of a C# record default (collection expressions are not allowed
    // as default parameter values).
    public MemoryProblem(int Id, string Title, string Summary, string Scope, string ProjectId, string Project,
        int SolutionCount, int AppliedCount, int VerifiedCount, DateTimeOffset LastChange)
        : this(Id, Title, Summary, Scope, ProjectId, Project, SolutionCount, AppliedCount, VerifiedCount, LastChange, new List<MemoryLesson>())
    {
    }
}

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
        _apiKey = ReadApiKey(
            Environment.GetEnvironmentVariable("MEMORY_API_KEY"),
            Environment.GetEnvironmentVariable("MEMORY_API_KEY_FILE"));
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    // Test seam: explicit gateway URL/key instead of environment variables.
    public MemoryRepository(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    internal static string? ReadApiKey(string? value, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            var fromFile = File.ReadAllText(filePath).Trim();
            return string.IsNullOrWhiteSpace(fromFile) ? null : fromFile;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
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
        // Recent-activity feeds mirror recent_prepares; absent on the old gateway
        // (null metrics node or missing keys) -> empty arrays via ArrayNodes.
        var recentReads = new List<object>();
        foreach (var r in ArrayNodes(metrics, "recent_reads"))
        {
            recentReads.Add(new
            {
                id = Str(Prop(r, "id")),
                title = Str(Prop(r, "title")),
                readAt = Str(Prop(r, "read_at"))
            });
        }
        var recentApplied = new List<object>();
        foreach (var r in ArrayNodes(metrics, "recent_applied"))
        {
            recentApplied.Add(new
            {
                id = Str(Prop(r, "id")),
                title = Str(Prop(r, "title")),
                appliedAt = Str(Prop(r, "applied_at"))
            });
        }
        var recentAdded = new List<object>();
        foreach (var r in ArrayNodes(metrics, "recent_added"))
        {
            recentAdded.Add(new
            {
                id = Str(Prop(r, "id")),
                title = Str(Prop(r, "title")),
                addedAt = Str(Prop(r, "added_at"))
            });
        }
        // Lessons are mapped after applied/verified events attach to them, so the
        // cards carry both the gateway's own counts and re-attached dead-link events.
        var lessonNodes = ArrayNodes(root, "lessons");
        var lessonCounters = MatchLessonEvents(ArrayNodes(root, "skill_events"), lessonNodes);
        var lessons = new List<MemoryLesson>();
        var index = 0;
        foreach (var node in lessonNodes)
        {
            lessons.Add(MapLesson(node, lessonCounters[index].Applied, lessonCounters[index].Verified));
            index++;
        }
        var problems = new List<MemoryProblem>();
        foreach (var node in ArrayNodes(root, "problems"))
            problems.Add(MapProblem(node));
        // The gateway stores one skill row per computer, so rows repeat per unique
        // skill name; MapSkills merges them into one card per name.
        var skills = MapSkills(ArrayNodes(root, "skills"));
        var events = new List<SkillEvent>();
        foreach (var node in ArrayNodes(root, "skill_events"))
            events.Add(MapSkillEvent(node));
        var metricObject = new
        {
            lessonsTotal = Int(Prop(metrics, "lessons_total")),
            appliedTotal = Int(Prop(metrics, "applied_total")),
            verifiedTotal = Int(Prop(metrics, "verified_total")),
            problemsTotal = Int(Prop(metrics, "problems_total")),
            // Unique skill names, not the gateway's per-computer row counter.
            skillsTotal = skills.Count,
            byAgent = MapCounters(Prop(metrics, "by_agent")),
            byProject = MapCounters(Prop(metrics, "by_project")),
            recentPrepares = prepares.ToArray(),
            recentReads = recentReads.ToArray(),
            recentApplied = recentApplied.ToArray(),
            recentAdded = recentAdded.ToArray(),
            note = Str(Prop(metrics, "note"))
        };
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

    private static MemoryLesson MapLesson(JsonElement e, int matchedApplied, int matchedVerified) => new(
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
        // The gateway counts applications/verifications by live links only; events
        // whose links point at removed lessons are re-attached by MatchLessonEvents,
        // which can raise the counters but never lower the gateway's own numbers.
        Max(Int(Prop(e, "applied_count")), matchedApplied),
        Max(Int(Prop(e, "verified_count")), matchedVerified),
        // Optional gateway fields: absent on the old gateway -> null/0, never a crash.
        Iso(Prop(e, "last_read_at")),
        Int(Prop(e, "read_count")),
        Iso(Prop(e, "last_applied_at")));

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
                Iso(Prop(e, "last_change")) ?? Epoch(),
                // The gateway returns each problem's own solutions as a lessons[] array;
                // absent on the old gateway -> empty list, never null.
                ArrayNodes(e, "lessons").Select(n => MapLesson(n, 0, 0)).ToList());

    // The gateway persists one row per (skill name, computer), so duplicates are
    // collapsed here: one MemorySkill per unique name (case-insensitive).
    private static List<MemorySkill> MapSkills(List<JsonElement> nodes)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var name = Str(Prop(node, "name"));
            if (name.Length == 0) continue;
            var key = name.ToLower();
            if (!groups.ContainsKey(key))
            {
                groups[key] = new List<JsonElement>();
                order.Add(key);
            }
            groups[key].Add(node);
        }
        var skills = new List<MemorySkill>();
        foreach (var key in order)
            skills.Add(MapSkillGroup(groups[key]));
        return skills;
    }

    private static MemorySkill MapSkillGroup(List<JsonElement> group)
    {
        // Every field except computer/version_count/project_id comes from the row
        // with the newest updated_at; computers are merged, version_count summed,
        // and a concrete project_id is preferred over the '*' bucket.
        JsonElement? freshest = null;
        var freshestUpdated = Epoch();
        var computers = new List<string>();
        var computersLower = new List<string>();
        var versionCount = 0;
        var projectId = "";
        var hasConcreteProject = false;
        foreach (var node in group)
        {
            var computer = Str(Prop(node, "computer"));
            if (computer.Length > 0)
            {
                var computerLower = computer.ToLower();
                var seen = false;
                foreach (var known in computersLower)
                {
                    if (known == computerLower) { seen = true; break; }
                }
                if (!seen)
                {
                    computers.Add(computer);
                    computersLower.Add(computerLower);
                }
            }
            versionCount += Int(Prop(node, "version_count"));
            var candidateProject = Str(Prop(node, "project_id"));
            if (candidateProject.Length > 0)
            {
                if (candidateProject != "*" && !hasConcreteProject)
                {
                    projectId = candidateProject;
                    hasConcreteProject = true;
                }
                else if (projectId.Length == 0)
                {
                    projectId = candidateProject;
                }
            }
            var updated = Iso(Prop(node, "updated_at")) ?? Epoch();
            if (freshest is null || updated > freshestUpdated)
            {
                freshest = node;
                freshestUpdated = updated;
            }
        }
        var dependencies = new List<string>();
        foreach (var d in ArrayNodes(freshest, "dependencies"))
            dependencies.Add(Str(d));
        return new MemorySkill(
            HashId(Str(Prop(freshest, "name"))),
            Str(Prop(freshest, "name")),
            Str(Prop(freshest, "description")),
            Str(Prop(freshest, "scope")),
            projectId,
            ProjectName(projectId),
            Str(Prop(freshest, "status")),
            Str(Prop(freshest, "source")),
            string.Join(", ", computers),
            Str(Prop(freshest, "version")),
            versionCount,
            dependencies.ToArray(),
            Str(Prop(freshest, "verified_example")),
            Iso(Prop(freshest, "registered_at")) ?? Epoch(),
            Iso(Prop(freshest, "updated_at")) ?? Epoch());
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

    // -------- applied/verified event -> lesson attachment --------

    private record LessonCounters(int Applied, int Verified);

    // Sentinels for the lesson indexes: no lesson matched, or several lessons
    // share the same fallback key (ambiguous -> events are not auto-attached).
    private static readonly int NoLesson = -1;
    private static readonly int Ambiguous = -2;

    // Counts applied/verified events per lesson: explicit links win, then
    // (project_id, method) after trim/casefold, then (project_id, method with
    // leading filler words stripped). Events that match nothing stay unassigned
    // (their counts are never invented) and each event lands on at most one lesson.
    private static List<LessonCounters> MatchLessonEvents(List<JsonElement> eventNodes, List<JsonElement> lessonNodes)
    {
        var counters = new List<LessonCounters>();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var byProjectMethod = new Dictionary<string, int>(StringComparer.Ordinal);
        var byProjectNormalized = new Dictionary<string, int>(StringComparer.Ordinal);
        var lesson = 0;
        foreach (var node in lessonNodes)
        {
            counters.Add(new LessonCounters(0, 0));
            var id = Str(Prop(node, "id"));
            if (id.Length > 0 && !byId.ContainsKey(id))
                byId[id] = lesson;
            IndexLessonKey(byProjectMethod, ProjectMethodKey(Str(Prop(node, "project_id")), Str(Prop(node, "method"))), lesson);
            IndexLessonKey(byProjectNormalized, ProjectMethodKey(Str(Prop(node, "project_id")), NormalizeMethod(Str(Prop(node, "method")))), lesson);
            lesson++;
        }
        foreach (var eventNode in eventNodes)
        {
            var kind = Str(Prop(eventNode, "kind")).ToLower();
            if (kind != "applied" && kind != "verified") continue;
            var hit = MatchEventToLesson(eventNode, byId, byProjectMethod, byProjectNormalized);
            if (hit < 0) continue;
            var countersNow = counters[hit];
            counters[hit] = kind == "applied"
                ? new LessonCounters(countersNow.Applied + 1, countersNow.Verified)
                : new LessonCounters(countersNow.Applied, countersNow.Verified + 1);
        }
        return counters;
    }

    private static void IndexLessonKey(Dictionary<string, int> index, string key, int lesson)
    {
        if (!index.ContainsKey(key))
        {
            index[key] = lesson;
            return;
        }
        var existing = index[key];
        if (existing != lesson && existing >= 0)
            index[key] = Ambiguous;
    }

    private static int MatchEventToLesson(JsonElement eventNode,
        Dictionary<string, int> byId, Dictionary<string, int> byProjectMethod, Dictionary<string, int> byProjectNormalized)
    {
        // (a) explicit links -> live lesson id.
        foreach (var link in ArrayNodes(eventNode, "links"))
        {
            var target = Str(link);
            if (target.Length > 0 && byId.ContainsKey(target))
                return byId[target];
        }
        var projectId = Str(Prop(eventNode, "project_id"));
        var method = Str(Prop(eventNode, "method"));
        // (b) exact (project_id, method) after trim/casefold.
        var exact = LookupIndex(byProjectMethod, ProjectMethodKey(projectId, method));
        if (exact >= 0) return exact;
        // (c) same project, method with leading stop-prefixes stripped ("В VS Code…" -> "vs code…").
        return LookupIndex(byProjectNormalized, ProjectMethodKey(projectId, NormalizeMethod(method)));
    }

    private static int LookupIndex(Dictionary<string, int> index, string key)
    {
        return index.ContainsKey(key) ? index[key] : NoLesson;
    }

    private static string ProjectMethodKey(string projectId, string method)
    {
        return projectId.ToLower() + "|" + method.ToLower();
    }

    // Leading filler cut from event method phrases before comparing with lesson
    // methods: "В VS Code…" -> "vs code…"; applied iteratively so stacked prefixes
    // ("использовать в VS Code…") unwind in order. Every prefix ends with a space,
    // so real words ("вставить", "installer") are never clipped.
    private static readonly string[] MethodStopPrefixes =
    {
        // русские предлоги
        "в ", "во ", "на ", "по ", "для ", "с ", "со ", "через ", "при ", "к ", "ко ", "из ", "от ", "о ", "об ",
        // русские обороты
        "использовать ", "использование ", "использован ", "используется ", "используя ",
        "установка ", "установить ", "установлен ", "настройка ", "настроить ", "настроен ",
        "запуск ", "запустить ", "запущен ", "выполнить ", "выполнена ", "выполнено ",
        "прочитать ", "прочитан ", "проверить ", "проверка ", "создать ", "создание ", "создан ",
        "починить ", "починка ", "ускорять ", "ускорить ",
        // английские артикли/предлоги
        "the ", "a ", "an ", "to ", "for ", "with ", "in ", "on ", "at ", "from ", "of ", "by ", "via ", "using ",
        // английские глаголы
        "use ", "install ", "installing ", "installed ", "setup ", "set up ", "run ", "running ", "ran ",
        "create ", "creating ", "created ", "configure ", "configuring ", "configured ",
        "enable ", "enabled ", "disable ", "disabled ", "fix ", "fixed ", "repair ", "repairing ",
        "check ", "checking ", "read ", "reading ", "how to ", "make ", "making ", "add ", "added ",
        "update ", "updating ", "updated ", "probe ", "probing ", "probed ", "test ", "testing ", "tested "
    };

    private static string NormalizeMethod(string method)
    {
        var text = method.Trim().ToLower();
        for (var pass = 0; pass < 8; pass++)
        {
            var stripped = text;
            foreach (var prefix in MethodStopPrefixes)
            {
                if (text.StartsWith(prefix) && text.Length > prefix.Length)
                {
                    var candidate = text.Substring(prefix.Length).TrimStart();
                    if (candidate.Length < stripped.Length) stripped = candidate;
                }
            }
            if (stripped == text) break;
            text = stripped;
        }
        // Never collapse to an empty key (would equate unrelated methods).
        return text.Length == 0 ? method.Trim().ToLower() : text;
    }

    private static int Max(int a, int b) => a > b ? a : b;

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
            recentReads = new object[] {},
            recentApplied = new object[] {},
            recentAdded = new object[] {},
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
