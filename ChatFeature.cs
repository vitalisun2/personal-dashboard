using System.Text.Json;

record ChatMessageRequest(string? Text);
record ChatMessage(Guid Id, string Role, string Text, DateTimeOffset CreatedAt);
record ChatSession(Guid Id, List<ChatMessage> Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
enum ChatActionType { CreateTask, UpdateTask, MoveTaskSection, RenameSection }
record ChatAction(ChatActionType Type, Guid? TaskId = null, string? Title = null, string? Description = null, string? Section = null, string? OldName = null, string? NewName = null);
record ChatResponse(string Reply, bool NeedsClarification, ChatAction? Action = null, TaskItem? Task = null);

sealed class ChatSessionStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public ChatSessionStore(IWebHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("CHAT_SESSIONS_FILE");
        if (!string.IsNullOrWhiteSpace(configured)) { _path = configured; return; }
        var tasksPath = Environment.GetEnvironmentVariable("TASKS_FILE");
        var dataDirectory = string.IsNullOrWhiteSpace(tasksPath) ? Path.Combine(environment.ContentRootPath, "data") : Path.GetDirectoryName(tasksPath);
        _path = Path.Combine(string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(environment.ContentRootPath, "data") : dataDirectory, "chat-sessions.json");
    }
    internal ChatSessionStore(string path) => _path = path;
    public async Task<ChatSession?> GetAsync(Guid id) { await _gate.WaitAsync(); try { return (await ReadUnsafe()).FirstOrDefault(x => x.Id == id); } finally { _gate.Release(); } }
    public async Task<ChatSession> CreateAsync(string text) { var now = DateTimeOffset.UtcNow; var session = new ChatSession(Guid.NewGuid(), [new ChatMessage(Guid.NewGuid(), "user", text, now)], now, now); await _gate.WaitAsync(); try { var all = (await ReadUnsafe()).ToList(); all.Add(session); await WriteUnsafeAsync(all); return session; } finally { _gate.Release(); } }
    public async Task<ChatSession?> AppendAsync(Guid id, params ChatMessage[] messages) { await _gate.WaitAsync(); try { var all = (await ReadUnsafe()).ToList(); var index = all.FindIndex(x => x.Id == id); if (index < 0) return null; var current = all[index]; var updated = current with { Messages = [.. current.Messages, .. messages], UpdatedAt = DateTimeOffset.UtcNow }; all[index] = updated; await WriteUnsafeAsync(all); return updated; } finally { _gate.Release(); } }
    private async Task<List<ChatSession>> ReadUnsafe() { if (!File.Exists(_path)) return []; await using var stream = File.OpenRead(_path); return await JsonSerializer.DeserializeAsync<List<ChatSession>>(stream, _json) ?? []; }
    private async Task WriteUnsafeAsync(List<ChatSession> sessions) { var directory = Path.GetDirectoryName(_path); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory); var temporary = _path + ".tmp"; await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, sessions, _json); File.Move(temporary, _path, true); }
}
static class ChatRoutes
{
    public static async Task<IResult> ProcessAsync(Guid? sessionId, ChatMessageRequest request, ChatSessionStore chats, TaskStore store, ITaskAgent agent)
    {
        var text = request.Text?.Trim(); if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { message = "Напишите сообщение для агента." });
        var session = sessionId is null ? await chats.CreateAsync(text) : await chats.GetAsync(sessionId.Value); if (session is null) return Results.NotFound();
        if (sessionId is not null) await chats.AppendAsync(session.Id, new ChatMessage(Guid.NewGuid(), "user", text, DateTimeOffset.UtcNow));
        var result = await HandleAsync(text, store, agent, sessionId is null); await chats.AppendAsync(session.Id, new ChatMessage(Guid.NewGuid(), "agent", result.Reply, DateTimeOffset.UtcNow));
        return Results.Ok(new { session = await chats.GetAsync(session.Id), response = result });
    }
    public static async Task<ChatResponse> HandleAsync(string text, TaskStore store, ITaskAgent agent, bool initialTaskEntry = false)
    {
        var tasks = await store.GetAllAsync(); var sections = tasks.Select(x => string.IsNullOrWhiteSpace(x.Section) ? "Общее" : x.Section).Append("Общее").Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); var lower = text.ToLowerInvariant();
        if (text.Length < 12 || lower is "привет" or "здравствуйте" or "помоги") return new ChatResponse("Уточните, что нужно сделать с задачами: создать задачу, изменить её или перенести в другой раздел?", true);
        if (TryParseExplicitAction(text, lower, tasks, out var explicitAction)) return await ApplyAsync(explicitAction!, store);
        if (lower.Contains("статус") || lower.Contains("сегодня") || lower.Contains("backlog") || lower.Contains("заверш"))
            return new ChatResponse("Статусы и перенос между Backlog/Сегодня меняются вручную на странице задач.", true);
        var conversationalQuestion = lower.Contains("как ") || lower.Contains("что ") || lower.Contains("почему") || lower.Contains("расскажи") || text.EndsWith('?');
        if (lower.Contains("задач") || lower.Contains("добавь") || lower.Contains("создай") || lower.Contains("запиши") || (initialTaskEntry && !conversationalQuestion))
        { var draft = await agent.CreateDraftAsync(text, sections); if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrWhiteSpace(draft.Description)) return new ChatResponse("Не хватает данных для задачи. Что именно нужно сделать?", true); return await ApplyAsync(new ChatAction(ChatActionType.CreateTask, Title: draft.Title, Description: draft.Description, Section: CanonicalSection(draft.Section, sections)), store); }
        return new ChatResponse(await agent.ChatAsync(text), false);
    }
    private static async Task<ChatResponse> ApplyAsync(ChatAction action, TaskStore store)
    {
        var validation = Validate(action); if (validation is not null) return new ChatResponse(validation, true);
        switch (action.Type)
        {
            case ChatActionType.CreateTask: var item = new TaskItem(Guid.NewGuid(), action.Title!.Trim(), action.Description!.Trim(), action.Section!.Trim(), TaskBucket.Backlog, TaskStatus.New, DateTimeOffset.UtcNow); await store.AddAsync(item); return new ChatResponse($"Задача «{item.Title}» добавлена в раздел «{item.Section}».", false, action, item);
            case ChatActionType.UpdateTask: var updated = await store.UpdateAsync(action.TaskId!.Value, task => task with { Title = action.Title?.Trim() ?? task.Title, Description = action.Description?.Trim() ?? task.Description, Section = action.Section?.Trim() ?? task.Section }); return updated is null ? new ChatResponse("Не нашёл такую задачу. Уточните её идентификатор.", true) : new ChatResponse("Задача обновлена.", false, action, updated);
            case ChatActionType.MoveTaskSection: var moved = await store.UpdateAsync(action.TaskId!.Value, task => task with { Section = action.Section!.Trim() }); return moved is null ? new ChatResponse("Не нашёл такую задачу. Уточните её идентификатор.", true) : new ChatResponse($"Задача перенесена в раздел «{moved.Section}».", false, action, moved);
            case ChatActionType.RenameSection: var renamed = await store.RenameSectionAsync(action.OldName!.Trim(), action.NewName!.Trim()); return new ChatResponse(renamed == 0 ? "Раздел не найден или уже имеет это название." : $"Раздел переименован. Изменено задач: {renamed}.", false, action);
            default: return new ChatResponse("Эта операция недоступна.", true);
        }
    }
    private static string? Validate(ChatAction action) => action.Type switch
    {
        ChatActionType.CreateTask when string.IsNullOrWhiteSpace(action.Title) || string.IsNullOrWhiteSpace(action.Description) || string.IsNullOrWhiteSpace(action.Section) => "Уточните заголовок, описание и раздел задачи.",
        ChatActionType.UpdateTask when action.TaskId is null || (string.IsNullOrWhiteSpace(action.Title) && string.IsNullOrWhiteSpace(action.Description) && string.IsNullOrWhiteSpace(action.Section)) => "Нужен идентификатор задачи и хотя бы одно поле для изменения.",
        ChatActionType.UpdateTask when action.Section is { Length: > 40 } => "Название раздела должно быть не длиннее 40 символов.",
        ChatActionType.MoveTaskSection when action.TaskId is null || string.IsNullOrWhiteSpace(action.Section) || action.Section.Length > 40 => "Нужен корректный идентификатор задачи и раздел длиной до 40 символов.",
        ChatActionType.RenameSection when string.IsNullOrWhiteSpace(action.OldName) || string.IsNullOrWhiteSpace(action.NewName) || action.NewName.Length > 40 => "Нужны непустые названия разделов длиной до 40 символов.",
        _ => null
    };
    private static string CanonicalSection(string section, IReadOnlyCollection<string> sections) => sections.FirstOrDefault(x => x.Equals(section?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? (string.IsNullOrWhiteSpace(section) ? "Общее" : section.Trim());
    private static bool TryParseExplicitAction(string text, string lower, IReadOnlyCollection<TaskItem> tasks, out ChatAction? action)
    {
        action = null;
        if ((lower.Contains("переименуй раздел") || lower.Contains("переименовать раздел")) && TrySplitRename(text, out var oldName, out var newName)) { action = new ChatAction(ChatActionType.RenameSection, OldName: oldName, NewName: newName); return true; }
        var id = tasks.FirstOrDefault(t => lower.Contains(t.Id.ToString().ToLowerInvariant()))?.Id;
        if (id is null || lower.Contains("статус") || lower.Contains("сегодня") || lower.Contains("backlog") || lower.Contains("заверш")) return false;
        if (lower.Contains("заголовок") || lower.Contains("название")) { var value = ValueAfterMarker(text, lower.Contains("заголовок") ? "заголовок" : "название"); if (!string.IsNullOrWhiteSpace(value)) action = new ChatAction(ChatActionType.UpdateTask, id, Title: value); }
        else if (lower.Contains("описание")) { var value = ValueAfterMarker(text, "описание"); if (!string.IsNullOrWhiteSpace(value)) action = new ChatAction(ChatActionType.UpdateTask, id, Description: value); }
        else if (lower.Contains("перенес") || lower.Contains("перемест") || lower.Contains("раздел")) { var marker = lower.IndexOf("раздел", StringComparison.Ordinal); var section = marker >= 0 ? text[(marker + 6)..].Trim(' ', ':', '-', '«', '»', '.') : null; if (!string.IsNullOrWhiteSpace(section)) action = new ChatAction(ChatActionType.MoveTaskSection, id, Section: section); }
        return action is not null;
    }
    private static string? ValueAfterMarker(string text, string marker) { var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (index < 0) return null; var value = text[(index + marker.Length)..].Trim(' ', ':', '-', '«', '»', '.'); var separator = value.IndexOf(" на ", StringComparison.OrdinalIgnoreCase); return (separator >= 0 ? value[(separator + 4)..] : value).Trim(' ', ':', '-', '«', '»', '.'); }
    private static bool TrySplitRename(string text, out string oldName, out string newName) { oldName = newName = string.Empty; var marker = text.IndexOf("раздел", StringComparison.OrdinalIgnoreCase); if (marker < 0) return false; var tail = text[(marker + 6)..].Trim(' ', ':', '«', '»'); var separator = tail.IndexOf(" в ", StringComparison.OrdinalIgnoreCase); if (separator < 0) separator = tail.IndexOf(" на ", StringComparison.OrdinalIgnoreCase); if (separator < 0) return false; oldName = tail[..separator].Trim(' ', '«', '»'); newName = tail[(separator + 3)..].Trim(' ', '«', '»', '.'); return oldName.Length > 0 && newName.Length > 0; }
}

static class ChatFeature
{
    public static IEndpointRouteBuilder MapChatRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/sessions", async (ChatMessageRequest request, ChatSessionStore chats, TaskStore store, ITaskAgent agent) =>
            await ChatRoutes.ProcessAsync(null, request, chats, store, agent));
        app.MapGet("/api/chat/sessions/{id:guid}", async (Guid id, ChatSessionStore chats) =>
            (await chats.GetAsync(id)) is { } session ? Results.Ok(session) : Results.NotFound());
        app.MapPost("/api/chat/sessions/{id:guid}/messages", async (Guid id, ChatMessageRequest request, ChatSessionStore chats, TaskStore store, ITaskAgent agent) =>
            await ChatRoutes.ProcessAsync(id, request, chats, store, agent));
        return app;
    }
}

