using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static TaskPrompt;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});
builder.Services.AddSingleton<TaskStore>();
builder.Services.AddSingleton<ITaskAgent>(_ => new LlmTaskAgent(
    providers: [
        new OllamaClient(),
        new OpenRouterClient(),
    ],
    fallback: new LocalTaskAgent()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/tasks", async (TaskStore store) => Results.Ok(await store.GetAllAsync()));

app.MapPost("/api/tasks", async (CreateTaskRequest request, TaskStore store, ITaskAgent agent) =>
{
    var text = request.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { message = "Описание задачи не может быть пустым." });

    var existing = await store.GetAllAsync();
    var sections = existing
        .Select(x => string.IsNullOrWhiteSpace(x.Section) ? "Общее" : x.Section)
        .Append("Общее")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var draft = await agent.CreateDraftAsync(text, sections);
    var item = new TaskItem(
        Guid.NewGuid(),
        draft.Title,
        draft.Description,
        draft.Section,
        TaskBucket.Backlog,
        TaskStatus.New,
        DateTimeOffset.UtcNow);

    await store.AddAsync(item);
    return Results.Created($"/api/tasks/{item.Id}", item);
});

app.MapPut("/api/tasks/{id:guid}/bucket", async (Guid id, MoveTaskRequest request, TaskStore store) =>
{
    var item = await store.UpdateAsync(id, task =>
    {
        var bucket = request.Bucket;
        var status = bucket == TaskBucket.Backlog ? TaskStatus.New : task.Status;
        return task with { Bucket = bucket, Status = status };
    });

    return item is null ? Results.NotFound() : Results.Ok(item);
});

app.MapPut("/api/tasks/{id:guid}/advance", async (Guid id, TaskStore store) =>
{
    var existing = await store.GetAsync(id);
    if (existing is null) return Results.NotFound();
    if (existing.Bucket != TaskBucket.Today)
        return Results.BadRequest(new { message = "Статус меняется только у задач в разделе Сегодня." });

    if (existing.Status == TaskStatus.Completed)
    {
        await store.DeleteAsync(id);
        return Results.Ok(new { deleted = true });
    }

    var next = existing.Status == TaskStatus.New ? TaskStatus.InProgress : TaskStatus.Completed;
    var updated = await store.UpdateAsync(id, task => task with { Status = next });
    return Results.Ok(updated);
});

app.MapFallbackToFile("index.html");
app.Run();

record CreateTaskRequest(string? Text);
record MoveTaskRequest(TaskBucket Bucket);
record TaskDraft(string Title, string Description, string Section);
record TaskItem(Guid Id, string Title, string Description, string Section, TaskBucket Bucket, TaskStatus Status, DateTimeOffset CreatedAt);

enum TaskBucket { Backlog, Today }
enum TaskStatus { New, InProgress, Completed }

interface ITaskAgent
{
    Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections);
}

// ── LLM-агент с каскадом провайдеров ────────────────────────────────────────
// Порядок: локальная Ollama → подписочный OpenRouter → эвристика.
// Каждый провайдер сам решает, доступен ли он (таймауты, отсутствие ключа);
// при сбое каскад уходит дальше, приложение всегда отвечает.

sealed class LlmTaskAgent : ITaskAgent
{
    private readonly ILlmProvider[] _providers;
    private readonly ITaskAgent _fallback;
    private readonly ILogger _logger;

    public LlmTaskAgent(ILlmProvider[] providers, ITaskAgent fallback)
    {
        _providers = providers;
        _fallback = fallback;
        var factory = LoggerFactory.Create(c => c.AddConsole());
        _logger = factory.CreateLogger("TaskAgent");
    }

    public async Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var draft = await provider.TryParseAsync(rawText, existingSections);
                if (draft is not null)
                {
                    // Страховка: маленькие модели льнут к «Общее»; если эвристика
                    // уверенно нашла конкретный раздел — берём её выбор.
                    if (draft.Section.Equals("Общее", StringComparison.OrdinalIgnoreCase))
                    {
                        var heuristicSection = LocalTaskAgent.FallbackSection(rawText, existingSections);
                        if (!heuristicSection.Equals("Общее", StringComparison.OrdinalIgnoreCase))
                            draft = draft with { Section = heuristicSection };
                    }
                    _logger.LogInformation("Задача разобрана провайдером {Provider}", provider.Name);
                    return draft;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Провайдер {Provider} упал: {Message}", provider.Name, ex.Message);
            }
        }
        _logger.LogInformation("Все LLM-провайдеры недоступны, используется эвристика");
        return await _fallback.CreateDraftAsync(rawText, existingSections);
    }
}

interface ILlmProvider
{
    string Name { get; }
    Task<TaskDraft?> TryParseAsync(string rawText, IReadOnlyCollection<string> existingSections);
}

abstract class HttpLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;

    protected HttpLlmProvider(HttpClient http) => _http = http;

    public abstract string Name { get; }
    protected abstract string ChatUrl { get; }
    protected virtual void AddAuth(HttpRequestMessage request) { }
    protected abstract object BuildPayload(string rawText, IReadOnlyCollection<string> existingSections);

    protected static string? ReadEnv(string name, string? fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    public virtual async Task<TaskDraft?> TryParseAsync(string rawText, IReadOnlyCollection<string> existingSections)
    {
        var body = BuildPayload(rawText, existingSections);
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        AddAuth(request);

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        return ExtractDraft(json, rawText);
    }

    // Достаёт {title, description, section} из тела ответа; при ошибке парсинга — null,
    // чтобы каскад ушёл на следующий провайдер / эвристику.
    private static TaskDraft? ExtractDraft(string body, string rawText)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;

            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            using var payload = JsonDocument.Parse(content[start..(end + 1)]);
            var root = payload.RootElement;

            var title = root.TryGetProperty("title", out var t) ? t.GetString()?.Trim() : null;
            var description = root.TryGetProperty("description", out var d) ? d.GetString()?.Trim() : null;
            var section = Sanitize(root.TryGetProperty("section", out var s) ? s.GetString() : null);

            if (string.IsNullOrWhiteSpace(title)) return null;

            const int maxTitle = 120;
            if (title.Length > maxTitle) title = title[..(maxTitle - 1)].TrimEnd() + "…";
            if (string.IsNullOrWhiteSpace(description)) description = rawText;
            if (string.IsNullOrWhiteSpace(section)) section = "Общее";

            return new TaskDraft(title, description, section);
        }
        catch
        {
            return null;
        }
    }

    // Модель иногда приклеивает к значению хвост грамматики ('}", ]} и т.п.) —
    // режем по первому невалидному символу.
    private static string? Sanitize(string? value)
    {
        if (value is null) return null;
        var cut = value.IndexOfAny(['"', '\'', '}', ']', '\\', '`']);
        if (cut >= 0) value = value[..cut];
        value = value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

// 1. Локальная Ollama (OpenAI-совместимый /v1/chat/completions, строгий JSON-грамматикой)
sealed class OllamaClient : HttpLlmProvider
{
    private const string DefaultUrl = "http://localhost:11434";
    private const string DefaultModel = "qwen3:4b-instruct-2507-q4_K_M";

    private readonly string _model;
    private readonly string _baseUrl;

    public OllamaClient()
        : base(new HttpClient { Timeout = TimeSpan.FromSeconds(35) })
    {
        _baseUrl = ReadEnv("OLLAMA_URL", DefaultUrl).TrimEnd('/');
        _model = ReadEnv("OLLAMA_MODEL", DefaultModel);
    }

    public override string Name => $"Ollama ({_model})";
    protected override string ChatUrl => $"{_baseUrl}/v1/chat/completions";

    protected override object BuildPayload(string rawText, IReadOnlyCollection<string> existingSections) => new
    {
        model = _model,
        temperature = 0,
        options = new { num_ctx = 16384 },
        messages = new[]
        {
            new { role = "system", content = BuildSystemPrompt(existingSections) },
            new { role = "user", content = rawText }
        },
        response_format = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "task_parse",
                strict = true,
                schema = Schema
            }
        }
    };
}

// 2. Подписочный OpenRouter (DeepSeek) — включается только при наличии ключа
sealed class OpenRouterClient : HttpLlmProvider
{
    private const string DefaultUrl = "https://openrouter.ai/api/v1";
    private const string DefaultModel = "deepseek/deepseek-v4-flash-0731";

    private readonly string _model;
    private readonly string? _apiKey;
    private readonly string _baseUrl;

    public OpenRouterClient()
        : base(new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
    {
        _baseUrl = ReadEnv("OPENROUTER_URL", DefaultUrl).TrimEnd('/');
        _model = ReadEnv("OPENROUTER_MODEL", DefaultModel);
        _apiKey = ReadEnv("OPENROUTER_API_KEY", null);
    }

    public override string Name => $"OpenRouter ({_model})";
    protected override string ChatUrl => $"{_baseUrl}/chat/completions";

    protected override void AddAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public override async Task<TaskDraft?> TryParseAsync(string rawText, IReadOnlyCollection<string> existingSections)
    {
        if (string.IsNullOrEmpty(_apiKey)) return null; // нет ключа — провайдер выключен
        return await base.TryParseAsync(rawText, existingSections);
    }

    protected override object BuildPayload(string rawText, IReadOnlyCollection<string> existingSections) => new
    {
        model = _model,
        temperature = 0,
        messages = new[]
        {
            new { role = "system", content = BuildSystemPrompt(existingSections) },
            new { role = "user", content = rawText }
        },
        response_format = new { type = "json_object" }
    };
}

// 3. Эвристика без сети: title из первого предложения, раздел — по ключевым словам
sealed class LocalTaskAgent : ITaskAgent
{
    public Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections)
    {
        var normalized = string.Join(' ', rawText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var firstSentenceEnd = normalized.IndexOfAny(['.', '!', '?']);
        var candidate = firstSentenceEnd > 0 ? normalized[..firstSentenceEnd] : normalized;
        var title = candidate.Length <= 72 ? candidate : candidate[..69].TrimEnd() + "…";
        var section = PickSection(normalized, existingSections);
        return Task.FromResult(new TaskDraft(title, normalized, section));
    }

    private static string PickSection(string text, IReadOnlyCollection<string> existingSections)
    {
        var sections = existingSections
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 1) Если пользователь явно назвал уже существующий раздел — используем его.
        var explicitSection = sections.FirstOrDefault(section =>
            text.Contains(section, StringComparison.OrdinalIgnoreCase));
        if (explicitSection is not null) return explicitSection;

        // 2) Небольшой локальный fallback. Выбираем только из существующих разделов.
        var lower = text.ToLowerInvariant();
        var hints = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Личный дашборд"] = ["дашборд", "dashboard", "интерфейс задач", "личное приложение"],
            ["Lost Cyber Hamster"] = ["hamster", "хомяк", "lost cyber", "lch", "париж", "барселон", "квест", "energy bar", "прыж"],
            ["Workflow"] = ["workflow", "агент", "инструкц", "prompt", "промпт", "оркестратор", "hindsight", "graphify"]
        };

        foreach (var (suggested, keywords) in hints)
        {
            if (!keywords.Any(lower.Contains)) continue;
            var existing = sections.FirstOrDefault(x => x.Equals(suggested, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
        }

        // 3) Если уверенного совпадения нет — дефолтный раздел.
        return sections.FirstOrDefault(x => x.Equals("Общее", StringComparison.OrdinalIgnoreCase)) ?? "Общее";
    }

    public static string FallbackSection(string text, IReadOnlyCollection<string> existingSections) =>
        PickSection(text, existingSections);
}

static class TaskPrompt
{
    public const string Instructions =
        "Ты — помощник личного дашборда задач. Из надиктованного пользователем текста задачи выдели: " +
        "короткий заголовок (2-6 слов, без точки в конце); подробное описание (1-3 предложения, " +
        "пересказ своими словами с сохранением всех деталей и фактов исходного текста); " +
        "раздел — выбери один из СПИСКА существующих разделов, если текст явно про него; " +
        "раздел «Общее» используй ТОЛЬКО если ни один из существующих разделов не подходит; " +
        "если подходящего раздела в списке нет — придумай новое короткое название (2-4 слова). " +
        "Верни строго JSON-объект вида {\"title\": \"...\", \"description\": \"...\", \"section\": \"...\"} " +
        "без markdown и лишнего текста.";

    public static string BuildSystemPrompt(IReadOnlyCollection<string> existingSections) =>
        Instructions + "\n\nСуществующие разделы: " + string.Join("; ", existingSections);

    public static readonly JsonElement Schema = JsonDocument.Parse(
        "{\"type\":\"object\",\"properties\":{" +
        "\"title\":{\"type\":\"string\",\"description\":\"Короткий заголовок задачи, 2-6 слов, без точки в конце\"}," +
        "\"description\":{\"type\":\"string\",\"description\":\"Подробное описание задачи, 1-3 предложения\"}," +
        "\"section\":{\"type\":\"string\",\"description\":\"Название раздела — из списка существующих или новое, 2-4 слова\"}}," +
        "\"required\":[\"title\",\"description\",\"section\"],\"additionalProperties\":false}").RootElement.Clone();
}

// Точка сборки для интеграционных тестов (WebApplicationFactory)
public partial class Program { }

sealed class TaskStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public TaskStore(IWebHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("TASKS_FILE");
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "tasks.json")
            : configured;
    }

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadUnsafeAsync(); }
        finally { _gate.Release(); }
    }

    public async Task<TaskItem?> GetAsync(Guid id)
    {
        await _gate.WaitAsync();
        try { return (await ReadUnsafeAsync()).FirstOrDefault(x => x.Id == id); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(TaskItem item)
    {
        await _gate.WaitAsync();
        try
        {
            var items = (await ReadUnsafeAsync()).ToList();
            items.Insert(0, item);
            await WriteUnsafeAsync(items);
        }
        finally { _gate.Release(); }
    }

    public async Task<TaskItem?> UpdateAsync(Guid id, Func<TaskItem, TaskItem> update)
    {
        await _gate.WaitAsync();
        try
        {
            var items = (await ReadUnsafeAsync()).ToList();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            items[index] = update(items[index]);
            await WriteUnsafeAsync(items);
            return items[index];
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var items = (await ReadUnsafeAsync()).Where(x => x.Id != id).ToList();
            await WriteUnsafeAsync(items);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<TaskItem>> ReadUnsafeAsync()
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        var items = await JsonSerializer.DeserializeAsync<List<TaskItem>>(stream, _json) ?? [];
        return items
            .Select(item => item with { Section = string.IsNullOrWhiteSpace(item.Section) ? "Общее" : item.Section })
            .ToList();
    }

    private async Task WriteUnsafeAsync(List<TaskItem> items)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, items, _json);
        File.Move(temp, _path, true);
    }
}