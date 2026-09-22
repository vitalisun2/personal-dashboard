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
builder.Services.AddSingleton<ChatSessionStore>();
builder.Services.AddSingleton<IMemoryRepository, MemoryRepository>();
builder.Services.AddSingleton<ITaskAgent>(_ => new LlmTaskAgent(
    providers: [
        new OpenRouterClient(),
        new OllamaClient(),
    ],
    fallback: new LocalTaskAgent()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/tasks", async (TaskStore store) => Results.Ok(await store.GetAllAsync()));

app.MapChatRoutes();

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

// Черновик не попадает в список задач, пока пользователь не подтвердит результат.
// Это позволяет безопасно уточнять формулировку сколько угодно раз.
app.MapPost("/api/tasks/draft", async (CreateTaskDraftRequest request, TaskStore store, ITaskAgent agent) =>
{
    var text = request.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { message = "Описание задачи не может быть пустым." });

    var sections = (await store.GetAllAsync())
        .Select(x => string.IsNullOrWhiteSpace(x.Section) ? "Общее" : x.Section)
        .Append("Общее")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(await agent.CreateDraftAsync(text, sections));
});

app.MapPost("/api/tasks/draft/revise", async (ReviseTaskDraftRequest request, TaskStore store, ITaskAgent agent) =>
{
    var correction = request.Correction?.Trim();
    if (!IsValidDraft(request.Draft))
        return Results.BadRequest(new { message = "Черновик задачи неполный. Создайте его заново." });
    if (string.IsNullOrWhiteSpace(correction))
        return Results.BadRequest(new { message = "Опишите, что нужно изменить." });

    var sections = (await store.GetAllAsync())
        .Select(x => string.IsNullOrWhiteSpace(x.Section) ? "Общее" : x.Section)
        .Append("Общее")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(await agent.ReviseDraftAsync(request.Draft!, correction, sections));
});

app.MapPost("/api/tasks/confirm", async (ConfirmTaskDraftRequest request, TaskStore store) =>
{
    if (!IsValidDraft(request.Draft))
        return Results.BadRequest(new { message = "Черновик задачи неполный. Создайте его заново." });

    var draft = request.Draft! with
    {
        Title = request.Draft!.Title.Trim(),
        Description = request.Draft.Description.Trim(),
        Section = request.Draft.Section.Trim()
    };
    var item = new TaskItem(Guid.NewGuid(), draft.Title, draft.Description, draft.Section,
        TaskBucket.Backlog, TaskStatus.New, DateTimeOffset.UtcNow);
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

// Удаление задачи (безвозвратно, из любого раздела)
app.MapDelete("/api/tasks/{id:guid}", async (Guid id, TaskStore store) =>
{
    await store.DeleteAsync(id);
    return Results.Ok(new { deleted = true });
});

// Обновление описания задачи
app.MapPut("/api/tasks/{id:guid}/description", async (Guid id, UpdateDescriptionRequest request, TaskStore store) =>
{
    var updated = await store.UpdateAsync(id, task => task with { Description = request.Description?.Trim() ?? task.Description });
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

// Обновление заголовка задачи
app.MapPut("/api/tasks/{id:guid}/title", async (Guid id, UpdateTitleRequest request, TaskStore store) =>
{
    var updated = await store.UpdateAsync(id, task => task with { Title = request.Title?.Trim() ?? task.Title });
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

// Перенос задачи в другой раздел текущей вкладки: вызывается перетаскиванием
// за ручку ☰, когда задача брошена в список другого раздела.
app.MapPut("/api/tasks/{id:guid}/section", async (Guid id, UpdateSectionRequest request, TaskStore store) =>
{
    var section = request.Section?.Trim();
    if (string.IsNullOrWhiteSpace(section))
        return Results.BadRequest(new { message = "Нужен раздел." });
    if (section.Length > 40)
        return Results.BadRequest(new { message = "Название раздела должно быть не длиннее 40 символов." });

    var updated = await store.UpdateAsync(id, task => task with { Section = section });
    return updated is not null ? Results.Ok(updated) : Results.NotFound();
});

// Агент-редактирование существующей задачи: черновик правки (title/description/section)
app.MapPost("/api/tasks/{id:guid}/edit", async (Guid id, EditTaskRequest request, TaskStore store, ITaskAgent agent) =>
{
    var item = await store.GetAsync(id);
    if (item is null) return Results.NotFound();

    var instruction = request.Text?.Trim();
    if (string.IsNullOrWhiteSpace(instruction))
        return Results.BadRequest(new { message = "Опишите, что нужно изменить в задаче." });

    var sections = (await store.GetAllAsync())
        .Select(x => string.IsNullOrWhiteSpace(x.Section) ? "Общее" : x.Section)
        .Append("Общее")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var draft = await agent.EditDraftAsync(new TaskDraft(item.Title, item.Description, item.Section), instruction, sections);
    return Results.Ok(draft);
});

// Подтверждение агент-правки: применяет к задаче заголовок, описание и раздел.
// Если модель вернула существующий раздел в другом регистре — используем каноничное имя.
app.MapPut("/api/tasks/{id:guid}/edit", async (Guid id, ConfirmTaskDraftRequest request, TaskStore store) =>
{
    if (!IsValidDraft(request.Draft))
        return Results.BadRequest(new { message = "Черновик правки неполный. Повторите редактирование." });

    var section = request.Draft!.Section.Trim();
    var existingSections = (await store.GetAllAsync())
        .Select(x => string.IsNullOrWhiteSpace(x.Section) ? "Общее" : x.Section)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var matchedSection = existingSections.FirstOrDefault(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
    if (matchedSection is not null) section = matchedSection;

    var updated = await store.UpdateAsync(id, task => task with
    {
        Title = request.Draft!.Title.Trim(),
        Description = request.Draft.Description.Trim(),
        Section = section
    });
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

// Переименование раздела: новое название применяется ко всем задачам этого раздела
app.MapPut("/api/tasks/sections/rename", async (RenameSectionRequest request, TaskStore store) =>
{
    var oldName = request.OldName?.Trim();
    var newName = request.NewName?.Trim();

    if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        return Results.BadRequest(new { message = "Название раздела не может быть пустым." });
    if (newName.Length > 40)
        return Results.BadRequest(new { message = "Название раздела должно быть не длиннее 40 символов." });

    var renamed = await store.RenameSectionAsync(oldName, newName);
    return Results.Ok(new { renamed });
});

// Порядок задач внутри раздела. Передаётся полный набор видимых задач раздела,
// чтобы сервер не мог случайно потерять задачу при устаревшем клиенте.
app.MapPut("/api/tasks/reorder", async (ReorderTasksRequest request, TaskStore store) =>
{
    var section = request.Section?.Trim();
    if (string.IsNullOrWhiteSpace(section) || request.TaskIds is null)
        return Results.BadRequest(new { message = "Нужны раздел и полный порядок задач." });

    var reordered = await store.ReorderTasksAsync(request.Bucket, section, request.TaskIds);
    return reordered
        ? Results.NoContent()
        : Results.BadRequest(new { message = "Состав задач изменился. Обновите список и повторите попытку." });
});

// Порядок разделов в текущей вкладке. Порядок задач внутри каждого раздела
// сохраняется, меняется только положение групп.
app.MapPut("/api/tasks/sections/reorder", async (ReorderSectionsRequest request, TaskStore store) =>
{
    if (request.Sections is null)
        return Results.BadRequest(new { message = "Нужен полный порядок разделов." });

    var reordered = await store.ReorderSectionsAsync(request.Bucket, request.Sections);
    return reordered
        ? Results.NoContent()
        : Results.BadRequest(new { message = "Состав разделов изменился. Обновите список и повторите попытку." });
});

// ---------------- Agent memory (mock repository, API-shaped) ----------------
app.MapGet("/api/memory/dashboard", async (IMemoryRepository memory) => Results.Ok(await memory.GetDashboard()));
app.MapGet("/api/memory/status", async (IMemoryRepository memory) => Results.Ok(await memory.GetStatus()));
app.MapGet("/api/memory/projects", async (IMemoryRepository memory) => Results.Ok(await memory.GetProjects()));
app.MapGet("/api/memory/agents", async (IMemoryRepository memory) => Results.Ok(await memory.GetAgents()));

app.MapGet("/api/memory/lessons", async (string? q, string? project, string? agent, IMemoryRepository memory) =>
    Results.Ok(await memory.SearchLessons(q, project, agent)));
app.MapGet("/api/memory/problems", async (string? q, string? project, IMemoryRepository memory) =>
    Results.Ok(await memory.SearchProblems(q, project)));
app.MapGet("/api/memory/skills", async (string? q, IMemoryRepository memory) =>
    Results.Ok(await memory.SearchSkills(q)));

app.MapFallbackToFile("index.html");
app.Run();

static bool IsValidDraft(TaskDraft? draft) => draft is not null
    && !string.IsNullOrWhiteSpace(draft.Title)
    && !string.IsNullOrWhiteSpace(draft.Description)
    && !string.IsNullOrWhiteSpace(draft.Section);

record CreateTaskRequest(string? Text);
record CreateTaskDraftRequest(string? Text);
record ReviseTaskDraftRequest(TaskDraft? Draft, string? Correction);
record ConfirmTaskDraftRequest(TaskDraft? Draft);
record UpdateDescriptionRequest(string? Description);
record UpdateTitleRequest(string? Title);
record UpdateSectionRequest(string? Section);
record EditTaskRequest(string? Text);
record MoveTaskRequest(TaskBucket Bucket);
record RenameSectionRequest(string? OldName, string? NewName);
record ReorderTasksRequest(TaskBucket Bucket, string? Section, IReadOnlyList<Guid>? TaskIds);
record ReorderSectionsRequest(TaskBucket Bucket, IReadOnlyList<string>? Sections);
record TaskDraft(string Title, string Description, string Section);
record TaskItem(Guid Id, string Title, string Description, string Section, TaskBucket Bucket, TaskStatus Status, DateTimeOffset CreatedAt);

enum TaskBucket { Backlog, Today }
enum TaskStatus { New, InProgress, Completed }

interface ITaskAgent
{
    Task<string> ChatAsync(string text);
    Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections);
    Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> existingSections);
    Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> existingSections);
}

// ── LLM-агент с каскадом провайдеров ────────────────────────────────────────
// Порядок: подписочный OpenRouter (DeepSeek) → локальная Ollama → эвристика.
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

    public async Task<string> ChatAsync(string text)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var reply = await provider.TryChatAsync(text);
                if (!string.IsNullOrWhiteSpace(reply)) return reply.Trim();
            }
            catch (Exception ex) { _logger.LogWarning("Провайдер {Provider} не обработал чат: {Message}", provider.Name, ex.Message); }
        }
        return await _fallback.ChatAsync(text);
    }

    public async Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> existingSections)
    {
        var revisionRequest = BuildRevisionRequest(draft, correction);
        foreach (var provider in _providers)
        {
            try
            {
                var revised = await provider.TryParseAsync(revisionRequest, existingSections);
                if (revised is not null)
                {
                    _logger.LogInformation("Черновик задачи уточнён провайдером {Provider}", provider.Name);
                    return revised;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Провайдер {Provider} не обработал правку: {Message}", provider.Name, ex.Message);
            }
        }
        _logger.LogInformation("Все LLM-провайдеры недоступны, правка добавлена локальным агентом");
        return await _fallback.ReviseDraftAsync(draft, correction, existingSections);
    }

    public async Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> existingSections)
    {
        var editRequest = BuildEditRequest(current, instruction);
        foreach (var provider in _providers)
        {
            try
            {
                var edited = await provider.TryParseAsync(editRequest, existingSections);
                if (edited is not null)
                {
                    // Страховка: при правке описания модель не должна «забывать» раздел
                    // и уводить задачу в «Общее», если пользователь про перенос не просил.
                    if (edited.Section.Equals("Общее", StringComparison.OrdinalIgnoreCase)
                        && !current.Section.Equals("Общее", StringComparison.OrdinalIgnoreCase)
                        && !LocalTaskAgent.MentionsSectionMove(instruction, existingSections))
                    {
                        edited = edited with { Section = current.Section };
                    }
                    _logger.LogInformation("Правка задачи подготовлена провайдером {Provider}", provider.Name);
                    return edited;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Провайдер {Provider} не подготовил правку: {Message}", provider.Name, ex.Message);
            }
        }
        _logger.LogInformation("Все LLM-провайдеры недоступны, правка задачи выполнена локальным агентом");
        return await _fallback.EditDraftAsync(current, instruction, existingSections);
    }
}

interface ILlmProvider
{
    string Name { get; }
    Task<TaskDraft?> TryParseAsync(string rawText, IReadOnlyCollection<string> existingSections);
    Task<string?> TryChatAsync(string text);
}

abstract class HttpLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;

    protected HttpLlmProvider(HttpClient http) => _http = http;

    public abstract string Name { get; }
    protected abstract string ChatUrl { get; }
    protected virtual void AddAuth(HttpRequestMessage request) { }
    protected abstract object BuildPayload(string rawText, IReadOnlyCollection<string> existingSections);
    protected abstract object BuildChatPayload(string text);

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

    public virtual async Task<string?> TryChatAsync(string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(BuildChatPayload(text)), Encoding.UTF8, "application/json")
        };
        AddAuth(request);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
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

// 2. Локальная Ollama (OpenAI-совместимый /v1/chat/completions, строгий JSON-грамматикой) — fallback
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

    protected override object BuildChatPayload(string text) => new
    {
        model = _model,
        temperature = 0.3,
        messages = new[]
        {
            new { role = "system", content = "Ты свободный помощник личного дашборда. Отвечай кратко и по существу на вопрос пользователя. Не выполняй никаких действий и не выдумывай доступ к внешним инструментам." },
            new { role = "user", content = text }
        }
    };
}

// 1. Подписочный OpenRouter (DeepSeek): основной разборщик, включается при наличии ключа
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

    public override async Task<string?> TryChatAsync(string text)
    {
        if (string.IsNullOrEmpty(_apiKey)) return null;
        return await base.TryChatAsync(text);
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

    protected override object BuildChatPayload(string text) => new
    {
        model = _model,
        temperature = 0.3,
        messages = new[]
        {
            new { role = "system", content = "Ты свободный помощник личного дашборда. Отвечай кратко и по существу на вопрос пользователя. Не выполняй никаких действий и не выдумывай доступ к внешним инструментам." },
            new { role = "user", content = text }
        }
    };
}

// 3. Эвристика без сети: title из первого предложения, раздел — по ключевым словам
sealed class LocalTaskAgent : ITaskAgent
{
    public Task<string> ChatAsync(string text) => Task.FromResult($"Вы спросили: {text.Trim()}\n\nЯ могу помочь разобраться с этим и выполнить только изменения в задачнике: создать, отредактировать или перенести задачу.");

    public Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections)
    {
        var normalized = string.Join(' ', rawText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var firstSentenceEnd = normalized.IndexOfAny(['.', '!', '?']);
        var candidate = firstSentenceEnd > 0 ? normalized[..firstSentenceEnd] : normalized;
        var title = candidate.Length <= 72 ? candidate : candidate[..69].TrimEnd() + "…";
        var section = PickSection(normalized, existingSections);
        return Task.FromResult(new TaskDraft(title, normalized, section));
    }

    public Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> existingSections)
    {
        // Без сети локальный агент сохраняет исходный результат и добавляет уточнение.
        // Явную замену заголовка поддерживаем отдельно, чтобы правка не ограничивалась
        // только описанием даже при недоступных LLM-провайдерах.
        var note = string.Join(' ', correction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var revisedTitle = TryExtractExplicitTitle(note) ?? draft.Title;
        return Task.FromResult(draft with
        {
            Title = revisedTitle,
            Description = $"{draft.Description}\n\nУточнение: {note}"
        });
    }

    public Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> existingSections)
    {
        // Без сети локальный агент применяет явные правки: замену заголовка и перенос
        // в раздел (существующий или новый). Прочие формулировочные правки сохраняются
        // как уточнение к описанию, чтобы результат был виден пользователю.
        var note = string.Join(' ', instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var revisedTitle = TryExtractExplicitTitle(note) ?? current.Title;
        var requestedSection = TryExtractSection(note, existingSections);
        var section = requestedSection ?? current.Section;

        var description = current.Description;
        if (revisedTitle.Equals(current.Title) && requestedSection is null)
            description = current.Description + "\n\nУточнение: " + note;
        return Task.FromResult(new TaskDraft(revisedTitle, description, section));
    }

    // Достаёт название раздела из правки: «перенеси в раздел Workflow», «создай раздел Дизайн».
    // Существующий раздел возвращается в каноничном написании; новый — как есть (до 40 символов).
    static string? TryExtractSection(string note, IReadOnlyCollection<string> existingSections)
    {
        var lower = note.ToLowerInvariant();
        var marker = lower.IndexOf("раздел", StringComparison.Ordinal);
        var markerLength = marker >= 0 ? "раздел".Length : -1;
        if (marker < 0)
        {
            marker = lower.IndexOf("секци", StringComparison.Ordinal);
            markerLength = marker >= 0 ? 5 : -1;
        }
        if (marker < 0) return null;

        var tail = note[(marker + markerLength)..].TrimStart(' ', ':', '-');
        if (tail.StartsWith("на", StringComparison.OrdinalIgnoreCase)) tail = tail[2..].TrimStart(' ', ':', '-');
        else if (tail.StartsWith("в ", StringComparison.OrdinalIgnoreCase)) tail = tail[2..].TrimStart(' ', ':', '-');

        var cut = tail.IndexOfAny(['.', ';', '\n', '!', '?']);
        if (cut < 0) cut = tail.IndexOf(" на ", StringComparison.OrdinalIgnoreCase);
        if (cut < 0) cut = tail.IndexOf(" чтобы", StringComparison.OrdinalIgnoreCase);
        if (cut < 0) cut = tail.IndexOf(" и ", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0) tail = tail[..cut].TrimEnd();
        if (tail.Length == 0) return null;
        if (tail.Length > 40) tail = tail[..40].TrimEnd();

        var existing = existingSections.FirstOrDefault(s => s.Equals(tail, StringComparison.OrdinalIgnoreCase));
        return existing is not null ? existing : tail;
    }

    // Правка явно просит перенести задачу или называет существующий раздел —
    // используется как признак того, что «Общее» от модели является осознанным выбором.
    public static bool MentionsSectionMove(string instruction, IReadOnlyCollection<string> existingSections)
    {
        var lower = instruction.ToLowerInvariant();
        string[] markers = ["раздел", "секци", "перенес", "перемест", "переведи"];
        if (markers.Any(lower.Contains)) return true;
        return existingSections.Any(section => section.Length > 2 && lower.Contains(section.ToLowerInvariant()));
    }

    private static string? TryExtractExplicitTitle(string correction)
    {
        var lower = correction.ToLowerInvariant();
        var marker = lower.IndexOf("заголовок", StringComparison.Ordinal);
        if (marker < 0) marker = lower.IndexOf("название", StringComparison.Ordinal);
        if (marker < 0) return null;

        var tail = correction[(marker + (lower[marker..].StartsWith("заголовок", StringComparison.Ordinal) ? "заголовок" : "название").Length)..].Trim();
        if (tail.StartsWith("на", StringComparison.OrdinalIgnoreCase)) tail = tail[2..].TrimStart(' ', ':', '-');
        else tail = tail.TrimStart(' ', ':', '-');
        if (tail.Length == 0) return null;

        var end = tail.IndexOfAny(['.', ';', '\n']);
        if (end >= 0) tail = tail[..end].TrimEnd();
        return tail.Length is > 0 and <= 120 ? tail : null;
    }

    private static string PickSection(string text, IReadOnlyCollection<string> existingSections)
    {
        var sections = existingSections
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 1) Если пользователь явно назвал уже существующий раздел — используем его.
        //    Берём САМЫЙ ДЛИННЫЙ подходящий, чтобы «Super Abilities UI» не схлопывался в «Super Abilities».
        var explicitSection = sections
            .Where(section => text.Contains(section, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(section => section.Length)
            .FirstOrDefault();
        if (explicitSection is not null) return explicitSection;

        // 2) Небольшой локальный fallback. Выбираем только из существующих разделов.
        var lower = text.ToLowerInvariant();
        var hints = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Личный дашборд"] = ["дашборд", "dashboard", "памят", "интерфейс", "личное приложение"],
            ["Lost Cyber Hamster"] = ["hamster", "хомяк", "lost cyber", "lch", "unity", "париж", "барселон", "квест", "energy bar", "прыж"],
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
        "короткий заголовок (2-6 слов, без точки в конце); лаконичное описание (1-3 коротких предложения, " +
        "кратко и по делу: только суть и факты исходного текста, ничего не теряя и ничего не добавляя; " +
        "без канцелярита, приветствий и лишних деталей); " +
        "раздел — выбери один из СПИСКА существующих разделов, если текст явно про него; " +
        "выбирай САМЫЙ ТОЧНЫЙ ПОЛНЫЙ вариант из списка: например «Super Abilities UI» — это отдельный раздел, " +
        "а не «Super Abilities»; " +
        "раздел «Общее» используй ТОЛЬКО если ни один из существующих разделов не подходит; " +
        "если подходящего раздела в списке нет — придумай новое короткое название (2-4 слова). " +
        "Верни строго JSON-объект вида {\"title\": \"...\", \"description\": \"...\", \"section\": \"...\"} " +
        "без markdown и лишнего текста.";

    public static string BuildSystemPrompt(IReadOnlyCollection<string> existingSections) =>
        Instructions + "\n\nСуществующие разделы: " + string.Join("; ", existingSections);

    public static string BuildRevisionRequest(TaskDraft draft, string correction) =>
        "Обнови черновик задачи по правке пользователя. Сохрани все детали, которые правка не отменяет. " +
        "Если правка просит перенести задачу в другой раздел или создать новый — обнови раздел. " +
        "Описание держи кратким и лаконичным: только суть и факты, без выдуманных деталей. " +
        "Верни только итоговый JSON по системной инструкции.\n\nТекущий черновик:\n" +
        JsonSerializer.Serialize(draft, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n\nПравка пользователя:\n" + correction;

    public static string BuildEditRequest(TaskDraft current, string instruction) =>
        "Обнови существующую задачу по указанию пользователя. Правка может касаться заголовка, описания или раздела. " +
        "Применяй только запрошенные изменения: не меняй заголовок, если пользователь просит изменить только описание, и наоборот. " +
        "Если правка просит перенести задачу в другой раздел — выбери его из списка существующих разделов; " +
        "если названного раздела нет — создай новое короткое название (2-4 слова). " +
        "Описание держи кратким и лаконичным: только суть и факты, без выдуманных деталей. " +
        "Верни только итоговый JSON по системной инструкции.\n\nТекущая задача:\n" +
        JsonSerializer.Serialize(current, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n\nУказание пользователя:\n" + instruction;

    public static readonly JsonElement Schema = JsonDocument.Parse(
        "{\"type\":\"object\",\"properties\":{" +
        "\"title\":{\"type\":\"string\",\"description\":\"Короткий заголовок задачи, 2-6 слов, без точки в конце\"}," +
        "\"description\":{\"type\":\"string\",\"description\":\"Подробное описание задачи, 1-3 предложения\"}," +
        "\"section\":{\"type\":\"string\",\"description\":\"Название раздела — из списка существующих или новое, 2-4 слова\"}}," +
        "\"required\":[\"title\",\"description\",\"section\"],\"additionalProperties\":false}").RootElement.Clone();
}

// Точка сборки для интеграционных тестов (WebApplicationFactory)
public partial class Program { }

// Mock repository kept as a spare reference implementation; the real one is MemoryRepository.cs.
sealed class MockMemoryRepository : IMemoryRepository
{
    private readonly List<MemoryLesson> _lessons =
    [
        new(1,"Диагностика collision window","bounds-overlap","Нестабильное определение столкновения на границах препятствий.","Unity 2D; BoxCollider2D рядом с краем препятствия.","Слишком большое окно допуска давало ложное столкновение.","Использовать фактический bounds overlap с уменьшенным tolerance и учитывать линию.","Проблемный сценарий перестал воспроизводиться после уменьшения допуска.","Повторный плейтест подтвердил корректное поведение.","project","lost-cyber-hamster-2025","Lost Cyber Hamster","active","Codex","Acer Nitro 16",DateTimeOffset.Now.AddDays(-3),12,9),
        new(2,"Graphify перед структурной правкой","graphify-first","Агент может изменить код, не заметив связанные компоненты.","Структурные изменения нескольких классов или подсистем.","Связи кода не всегда очевидны из одного файла.","Перед правкой проверить graphify-out и затем сверить реальные исходники.","Несколько правок были скорректированы до изменения кода после обнаружения зависимостей.","Validation прошёл без регрессий.","global","*","Общий опыт","active","Codex","Acer Nitro 16",DateTimeOffset.Now.AddDays(-5),9,8),
        new(3,"Root-cause после повторной ошибки","root-cause-mode","Агент повторяет похожее исправление после первой неудачи.","Предыдущая попытка не решила проблему.","Недостаток нового evidence перед следующей гипотезой.","После повторной ошибки переключаться в root-cause mode и требовать новый факт.","Корневая причина выявлялась быстрее и не повторялись одинаковые правки.","Результат подтверждался отдельной проверкой.","global","*","Общий опыт","active","Hermes","PC-2",DateTimeOffset.Now.AddDays(-6),7,5),
        new(4,"Hindsight recall перед изменением","memory-recall","Накопленный опыт не используется перед новой похожей задачей.","Перед существенной работой по знакомой области.","Работа начинается без извлечения релевантного опыта.","Выполнять релевантный recall перед существенной работой.","Повторно использовались уже проверенные методы.","Метод применялся в нескольких задачах.","global","*","Общий опыт","active","Hermes","PC-2",DateTimeOffset.Now.AddDays(-8),5,4),
        new(5,"UI Toolkit: защита от сквозного клика","ui-event-guard","Клик по закрывающейся модалке проходит в нижележащий экран.","Unity UI Toolkit; смена экранов в рамках pointer event.","Событие продолжает распространяться после изменения UI.","Останавливать propagation и разводить переключение экранов по безопасному жизненному циклу.","Сквозной переход перестал воспроизводиться.","Проверено на Win modal.","project","lost-cyber-hamster-2025","Lost Cyber Hamster","active","Copilot","Acer Nitro 16",DateTimeOffset.Now.AddDays(-1),4,4),
        new(6,"Идемпотентная запись опыта","idempotent-memory-write","Одинаковый урок может создаваться несколько раз при retry.","Общая память агентов на нескольких машинах.","Повторная доставка не имеет стабильного idempotency key.","Формировать стабильный ключ по событию и проверять существующую запись перед insert.","Повторные доставки не создают дубли.","Проверено на тестовом retry очереди.","project","agent-memory-system","Общая память агентов","active","Codex","Acer Nitro 16",DateTimeOffset.Now.AddHours(-12),6,6)
    ];

    private readonly List<MemoryProblem> _problems =
    [
        new(1,"Нестабильные collision-проверки Unity","Ложные столкновения на краях препятствий.","project","lost-cyber-hamster-2025","Lost Cyber Hamster",3,17,12,DateTimeOffset.Now.AddDays(-1)),
        new(2,"Повторение нерабочих агентских правок","Следующая попытка повторяет гипотезу без нового evidence.","global","*","Общий опыт",2,9,7,DateTimeOffset.Now.AddDays(-2)),
        new(3,"Сквозные клики UI Toolkit","Pointer event срабатывает на следующем экране после закрытия модалки.","project","lost-cyber-hamster-2025","Lost Cyber Hamster",2,6,5,DateTimeOffset.Now.AddDays(-1)),
        new(4,"Дублирование опыта между машинами","Одинаковый урок может сохраняться несколькими агентами.","project","agent-memory-system","Общая память агентов",3,8,6,DateTimeOffset.Now.AddHours(-10))
    ];

    private readonly List<MemorySkill> _skills =
    [
        new(1,"graphify-use","Чтение графа связей перед структурными изменениями.","global","*","Общий опыт","active","skills/graphify-use","Acer Nitro 16","v3",3,[],"Использован перед рефакторингом bot strategies.",DateTimeOffset.Now.AddMonths(-1),DateTimeOffset.Now.AddDays(-2)),
        new(2,"unity-validation","Проверка Unity-изменений принятой validation-последовательностью.","project","lost-cyber-hamster-2025","Lost Cyber Hamster","active","skills/unity-validation","Acer Nitro 16","v5",5,[],"Validation после исправления double jump.",DateTimeOffset.Now.AddMonths(-2),DateTimeOffset.Now.AddDays(-1)),
        new(3,"memory-recall","Извлечение релевантного опыта перед существенной работой.","global","*","Общий опыт","active","skills/memory-recall","PC-2","v4",4,[],"Recall перед изменением agent pipeline.",DateTimeOffset.Now.AddMonths(-1),DateTimeOffset.Now.AddHours(-12)),
        new(4,"legacy-memory-sync","Старый механизм синхронизации общей памяти.","global","*","Общий опыт","obsolete","skills/legacy-memory-sync","PC-2","v1",1,[],"Устаревший пример.",DateTimeOffset.Now.AddMonths(-4),DateTimeOffset.Now.AddMonths(-1))
    ];

    private readonly List<SkillEvent> _events =
    [
        new("skill_used","graphify-use","Проверен граф связей","Validation успешен","Codex","Acer Nitro 16",DateTimeOffset.Now.AddHours(-5),"lost-cyber-hamster-2025"),
        new("skill_candidate","midnight-reset-check","Замечена повторяемая проверка reset","Кандидат","Hermes","PC-2",DateTimeOffset.Now.AddDays(-1),"lost-cyber-hamster-2025")
    ];

    public async Task<IReadOnlyList<string>> GetProjects() => _lessons.Select(x => x.Project).Concat(_problems.Select(x => x.Project)).Distinct().Order().ToArray();
        public async Task<IReadOnlyList<string>> GetAgents() => _lessons.Select(x => x.Agent).Distinct().Order().ToArray();

        public async Task<IEnumerable<MemoryLesson>> SearchLessons(string? q, string? project, string? agent)
    {
        var query = _lessons.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(project)) query = query.Where(x => x.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(agent)) query = query.Where(x => x.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Mock lexical fallback. Replace with Hindsight/vector semantic search in the real repository.
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(x => words.Any(w => SearchText(x).Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        return query.OrderByDescending(x => x.OccurredAt);
    }

    public async Task<IEnumerable<MemoryProblem>> SearchProblems(string? q, string? project)
    {
        var query = _problems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(project)) query = query.Where(x => x.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => (x.Title + " " + x.Summary).Contains(q, StringComparison.OrdinalIgnoreCase));
        return query.OrderByDescending(x => x.LastChange);
    }

    public async Task<IEnumerable<MemorySkill>> SearchSkills(string? q)
    {
        var query = _skills.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => (x.Name + " " + x.Description + " " + x.Project).Contains(q, StringComparison.OrdinalIgnoreCase));
        return query.OrderBy(x => x.Status == "active" ? 0 : 1).ThenBy(x => x.Name);
    }

    public async Task<object> GetDashboard()
    {
        var recentPrepares = new[]
        {
            new { agent="Codex", computer="Acer Nitro 16", occurredAt=DateTimeOffset.Now.AddMinutes(-20), projectId="lost-cyber-hamster-2025", task="Исправить double jump", foundRecords=3, warning=(string?)null },
            new { agent="Hermes", computer="PC-2", occurredAt=DateTimeOffset.Now.AddHours(-2), projectId="agent-memory-system", task="Обновить skill sync", foundRecords=2, warning=(string?)null },
            new { agent="Copilot", computer="Acer Nitro 16", occurredAt=DateTimeOffset.Now.AddHours(-5), projectId="lost-cyber-hamster-2025", task="UI Toolkit navigation", foundRecords=1, warning=(string?)null }
        };

        return new
        {
            generatedAt = DateTimeOffset.Now,
            lessons = _lessons,
            problems = _problems,
            skills = _skills,
            skillEvents = _events,
            metrics = new
            {
                lessonsTotal = _lessons.Count,
                appliedTotal = _lessons.Sum(x => x.AppliedCount),
                verifiedTotal = _lessons.Sum(x => x.VerifiedCount),
                problemsTotal = _problems.Count,
                skillsTotal = _skills.Count,
                byAgent = _lessons.GroupBy(x => x.Agent).ToDictionary(g => g.Key, g => new { lessons=g.Count(), applied=g.Sum(x=>x.AppliedCount), verified=g.Sum(x=>x.VerifiedCount) }),
                byProject = _lessons.GroupBy(x => x.Project).ToDictionary(g => g.Key, g => new { lessons=g.Count(), applied=g.Sum(x=>x.AppliedCount), verified=g.Sum(x=>x.VerifiedCount) }),
                recentPrepares,
                note = "Моковые данные для фронтенда. Реальный репозиторий должен вернуть контракт Agent Memory API."
            },
            window = "Демо-окно данных"
        };
    }

    public async Task<object> GetStatus() => new
    {
        database = new { status = "healthy", requiresAttention = false },
        hindsight = new { status = "healthy", requiresAttention = false },
        worker = new { status = "idle", requiresAttention = false, hasLastError = false },
        queue = new { queued = 2, processing = 0, completed = 41, failed = 0 },
        jobs = new[] { new { id="lesson-delivery-482", status="completed" }, new { id="skills-sync-129", status="processing" } },
        computers = new[] { new { name="Acer Nitro 16", status="online" }, new { name="PC-2", status="known" } }
    };

    private static string SearchText(MemoryLesson x) => string.Join(' ', x.Title, x.Method, x.Problem, x.Conditions, x.Cause, x.WorkingMethod, x.Evidence, x.Verification, x.Project, x.Agent);
}

sealed class TaskStore
{
    private const int ArchiveRetentionDays = 30;
    private readonly string _path;
    private readonly bool _protectionEnabled;
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
        _protectionEnabled = string.Equals(
            Environment.GetEnvironmentVariable("TASKS_PROTECTION_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    // Test seam: explicit file path instead of the TASKS_FILE env var / server environment.
    public TaskStore(string path)
    {
        _path = path;
    }

    // Test seam for production-like recovery and archiving behavior.
    internal TaskStore(string path, bool protectionEnabled)
    {
        _path = path;
        _protectionEnabled = protectionEnabled;
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

    // Переименование раздела во всех задачах. Сопоставление без учёта регистра;
    // имя пишется с обрезкой. Возвращает число задач, чьё название фактически изменилось.
    public async Task<int> RenameSectionAsync(string oldName, string newName)
    {
        oldName = oldName.Trim();
        newName = newName.Trim();
        if (oldName.Length == 0 || newName.Length == 0) return 0;

        await _gate.WaitAsync();
        try
        {
            var items = await ReadUnsafeAsync();
            var changed = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var current = items[i].Section;
                if (!current.Equals(oldName, StringComparison.OrdinalIgnoreCase)) continue;
                if (current.Equals(newName, StringComparison.Ordinal)) continue;
                items[i] = items[i] with { Section = newName };
                changed++;
            }
            if (changed > 0) await WriteUnsafeAsync(items);
            return changed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReorderTasksAsync(TaskBucket bucket, string section, IReadOnlyList<Guid> orderedIds)
    {
        section = section.Trim();
        if (section.Length == 0 || orderedIds.Distinct().Count() != orderedIds.Count) return false;

        await _gate.WaitAsync();
        try
        {
            var items = await ReadUnsafeAsync();
            var current = items
                .Where(x => x.Bucket == bucket && x.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (current.Length != orderedIds.Count || !current.Select(x => x.Id).ToHashSet().SetEquals(orderedIds)) return false;

            var replacement = current.ToDictionary(x => x.Id);
            var next = 0;
            for (var i = 0; i < items.Count; i++)
                if (items[i].Bucket == bucket && items[i].Section.Equals(section, StringComparison.OrdinalIgnoreCase))
                    items[i] = replacement[orderedIds[next++]];

            await WriteUnsafeAsync(items);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReorderSectionsAsync(TaskBucket bucket, IReadOnlyList<string> orderedSections)
    {
        var requested = orderedSections.Select(x => x?.Trim() ?? string.Empty).ToArray();
        if (requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            return false;

        await _gate.WaitAsync();
        try
        {
            var items = await ReadUnsafeAsync();
            var currentSections = items
                .Where(x => x.Bucket == bucket)
                .Select(x => x.Section)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (currentSections.Length != requested.Length || !currentSections.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(requested))
                return false;

            var orderedItems = requested
                .SelectMany(section => items.Where(x => x.Bucket == bucket && x.Section.Equals(section, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var next = 0;
            for (var i = 0; i < items.Count; i++)
                if (items[i].Bucket == bucket)
                    items[i] = orderedItems[next++];

            await WriteUnsafeAsync(items);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<TaskItem>> ReadUnsafeAsync()
    {
        if (!File.Exists(_path))
        {
            if (!_protectionEnabled) return [];

            var backup = BackupPath();
            if (!File.Exists(backup))
                throw new InvalidOperationException($"Task data file is missing: {_path}. Restore it from archive or {backup}.");

            File.Copy(backup, _path, overwrite: false);
        }

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

        if (_protectionEnabled && File.Exists(_path))
            File.Replace(temp, _path, BackupPath(), ignoreMetadataErrors: true);
        else
            File.Move(temp, _path, true);

        if (_protectionEnabled)
            await ArchiveCurrentAsync();
    }

    private string BackupPath() => _path + ".bak";

    private async Task ArchiveCurrentAsync()
    {
        var dataDirectory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException($"Task data path has no directory: {_path}");
        var archiveDirectory = Path.Combine(dataDirectory, "archive");
        var snapshotDirectory = Path.Combine(archiveDirectory, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(snapshotDirectory);

        var snapshot = Path.Combine(snapshotDirectory, Path.GetFileName(_path));
        await using var source = File.OpenRead(_path);
        await using var destination = File.Create(snapshot);
        await source.CopyToAsync(destination);

        var datedDirectories = Directory.EnumerateDirectories(archiveDirectory)
            .Select(path => new { Path = path, Name = Path.GetFileName(path) })
            .Where(x => DateOnly.TryParseExact(x.Name, "yyyy-MM-dd", out _))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .Skip(ArchiveRetentionDays);

        foreach (var directory in datedDirectories)
            Directory.Delete(directory.Path, recursive: true);
    }
}
