using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentChat;

public enum ChatScope { Tasks, Knowledge }
public enum ChatModel { DeepSeek, Gemma }
public sealed record ChatMessageRequest(string? Text, string? Model = null);
public sealed record ChatMessage(Guid Id, string Role, string Text, DateTimeOffset CreatedAt);
public sealed record ChatPending(string Kind, string Data);
public sealed record ChatTurn(string Text, bool IsInitial, ChatPending? Pending, IReadOnlyList<ChatMessage> History, ChatModel Model = ChatModel.DeepSeek);
public sealed record ChatSession(Guid Id, ChatScope Scope, List<ChatMessage> Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ChatPending? Pending = null);
public sealed record ChatReply(string Text, bool NeedsClarification, bool ChangedData = false, ChatPending? Pending = null);

public interface IChatCommandFacade
{
    ChatScope Scope { get; }
    Task<ChatReply> HandleAsync(string text, bool initialPrompt, CancellationToken cancellationToken);
}
public interface IChatConversationFacade : IChatCommandFacade
{
    Task<ChatReply> HandleAsync(ChatTurn turn, CancellationToken cancellationToken);
}
public interface IChatResponder
{
    Task<string> ReplyAsync(string text, IReadOnlyList<ChatMessage>? history, CancellationToken cancellationToken);
}

public interface IChatSessionStore
{
    Task<ChatSession?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ChatSession> CreateAsync(ChatScope scope, string? initialText, CancellationToken cancellationToken);
    Task<ChatSession?> AppendAsync(Guid id, params ChatMessage[] messages);
    Task<ChatSession?> SetPendingAsync(Guid id, ChatPending? pending, CancellationToken cancellationToken);
}

/// <summary>Short-lived conversations exist only in this process and expire after inactivity.</summary>
public sealed class EphemeralChatSessionStore : IChatSessionStore
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    private readonly Dictionary<Guid, ChatSession> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ChatSession?> GetAsync(Guid id, CancellationToken ct)
    { await _gate.WaitAsync(ct); try { return GetActiveUnsafe(id); } finally { _gate.Release(); } }

    public async Task<ChatSession> CreateAsync(ChatScope scope, string? initialText, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var messages = string.IsNullOrWhiteSpace(initialText) ? [] : new List<ChatMessage> { new(Guid.NewGuid(), "user", initialText.Trim(), now) };
        var session = new ChatSession(Guid.NewGuid(), scope, messages, now, now);
        await _gate.WaitAsync(ct); try { PruneExpiredUnsafe(now); _sessions.Add(session.Id, session); return session; } finally { _gate.Release(); }
    }

    public async Task<ChatSession?> AppendAsync(Guid id, params ChatMessage[] messages)
    { await _gate.WaitAsync(); try { var current = GetActiveUnsafe(id); if (current is null) return null; var updated = current with { Messages = [.. current.Messages, .. messages], UpdatedAt = DateTimeOffset.UtcNow }; _sessions[id] = updated; return updated; } finally { _gate.Release(); } }

    public async Task<ChatSession?> SetPendingAsync(Guid id, ChatPending? pending, CancellationToken ct)
    { await _gate.WaitAsync(ct); try { var current = GetActiveUnsafe(id); if (current is null) return null; var updated = current with { Pending = pending, UpdatedAt = DateTimeOffset.UtcNow }; _sessions[id] = updated; return updated; } finally { _gate.Release(); } }

    private ChatSession? GetActiveUnsafe(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return null;
        if (DateTimeOffset.UtcNow - session.UpdatedAt < IdleLifetime) return session;
        _sessions.Remove(id);
        return null;
    }

    private void PruneExpiredUnsafe(DateTimeOffset now)
    { foreach (var id in _sessions.Where(x => now - x.Value.UpdatedAt >= IdleLifetime).Select(x => x.Key).ToArray()) _sessions.Remove(id); }
}

public sealed class ChatService(IChatSessionStore sessions, IEnumerable<IChatCommandFacade> facades)
{
    private const int MaxUserTurns = 20;
    private const int MaxConversationCharacters = 32_000;
    private readonly IReadOnlyDictionary<ChatScope, IChatCommandFacade> _facades = facades.ToDictionary(x => x.Scope);

    public Task<ChatSession?> GetAsync(Guid id, ChatScope scope, CancellationToken ct) => GetScopedAsync(id, scope, ct);
    private async Task<ChatSession?> GetScopedAsync(Guid id, ChatScope scope, CancellationToken ct)
    { var session = await sessions.GetAsync(id, ct); return session is { Scope: var actual } && actual == scope ? session : null; }

    public async Task<(ChatSession? Session, ChatReply? Reply, string? Error)> SendAsync(Guid? id, ChatScope scope, ChatMessageRequest request, CancellationToken ct)
    {
        var text = request.Text?.Trim();
        var model = request.Model?.Trim().ToLowerInvariant() switch
        {
            null or "" or "deepseek" => ChatModel.DeepSeek,
            "gemma" => ChatModel.Gemma,
            _ => (ChatModel?)null
        };
        if (model is null) return (null, null, "Неизвестная модель чата. Выберите DeepSeek или Gemma.");
        if (!_facades.TryGetValue(scope, out var facade)) return (null, null, "Область чата недоступна.");
        ChatSession? session;
        var initial = id is null;
        if (id is null && string.IsNullOrWhiteSpace(text))
            return (await sessions.CreateAsync(scope, null, ct), null, null);
        if (string.IsNullOrWhiteSpace(text)) return (null, null, "Напишите сообщение для агента.");
        if (text.Length > MaxConversationCharacters) return (null, null, "Сообщение слишком длинное для этой беседы.");
        if (id is null) session = await sessions.CreateAsync(scope, text, ct);
        else { session = await sessions.GetAsync(id.Value, ct); if (session is null || session.Scope != scope) return (null, null, "Сессия не найдена в этой области."); if (session.Messages.Count(x => x.Role == "user") >= MaxUserTurns) return (session, null, "Лимит беседы — 20 ваших сообщений. Начните новый чат."); if (session.Messages.Sum(x => x.Text.Length) + text.Length > MaxConversationCharacters) return (session, null, "Контекст беседы слишком большой. Начните новый чат."); session = await sessions.AppendAsync(session.Id, new ChatMessage(Guid.NewGuid(), "user", text, DateTimeOffset.UtcNow)); if (session is null) return (null, null, "Сессия не найдена в этой области."); }
        var reply = facade is IChatConversationFacade conversationFacade
            ? await conversationFacade.HandleAsync(new ChatTurn(text, initial, session.Pending, session.Messages, model.Value), ct)
            : await facade.HandleAsync(text, initial, ct);
        var updated = await sessions.AppendAsync(session.Id, new ChatMessage(Guid.NewGuid(), "agent", reply.Text, DateTimeOffset.UtcNow));
        if (updated is not null) updated = await sessions.SetPendingAsync(updated.Id, reply.Pending, ct);
        return (updated, reply, null);
    }
}

public static class ChatModuleExtensions
{
    public static IServiceCollection AddAgentChat(this IServiceCollection services)
    { services.AddSingleton<IChatSessionStore, EphemeralChatSessionStore>(); services.AddSingleton<ChatService>(); return services; }

    public static IEndpointRouteBuilder MapAgentChat(this IEndpointRouteBuilder app)
    {
        MapScope(app, "tasks", ChatScope.Tasks); MapScope(app, "knowledge", ChatScope.Knowledge); return app;
    }

    private static void MapScope(IEndpointRouteBuilder app, string route, ChatScope scope)
    {
        app.MapPost($"/api/{route}/chat/sessions", async (ChatMessageRequest request, ChatService service, CancellationToken ct) =>
            ToResult(await service.SendAsync(null, scope, request, ct)));
        app.MapGet($"/api/{route}/chat/sessions/{{id:guid}}", async (Guid id, ChatService service, CancellationToken ct) =>
            (await service.GetAsync(id, scope, ct)) is { } session ? Results.Ok(session) : Results.NotFound());
        app.MapPost($"/api/{route}/chat/sessions/{{id:guid}}/messages", async (Guid id, ChatMessageRequest request, ChatService service, CancellationToken ct) =>
            ToResult(await service.SendAsync(id, scope, request, ct)));
    }

    private static IResult ToResult((ChatSession? Session, ChatReply? Reply, string? Error) result) => result.Error is not null ? Results.BadRequest(new { message = result.Error }) : Results.Ok(new { session = result.Session, reply = result.Reply, response = result.Reply });
}
