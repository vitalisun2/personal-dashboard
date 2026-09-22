using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentChat;
using KnowledgeBase.Api.Application;
using TaskBoard;
using TaskBoard.Application;

sealed class TaskChatFacade(TaskChatService tasks) : IChatConversationFacade
{
    public ChatScope Scope => ChatScope.Tasks;
    public async Task<ChatReply> HandleAsync(string text, bool initialPrompt, CancellationToken cancellationToken)
    {
        var result = await tasks.HandleAsync(text, initialPrompt, cancellationToken);
        return new ChatReply(result.Reply, result.NeedsClarification, result.Action is not null);
    }

    public async Task<ChatReply> HandleAsync(ChatTurn turn, CancellationToken cancellationToken)
    {
        var history = turn.History.Select(message => new TaskConversationMessage(message.Role, message.Text)).ToArray();
        var result = await tasks.HandleAsync(turn.Text, turn.IsInitial, cancellationToken, history);
        return new ChatReply(result.Reply, result.NeedsClarification, result.Action is not null);
    }
}

/// <summary>Read-only conversation adapter. It has no dependency on either write API.</summary>
sealed class ReadOnlyChatResponder : IChatResponder
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<string> ReplyAsync(string text, IReadOnlyList<ChatMessage>? history, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            var url = (Environment.GetEnvironmentVariable("OPENROUTER_URL") ?? "https://openrouter.ai/api/v1").TrimEnd('/') + "/chat/completions";
            var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "deepseek/deepseek-v4-flash-0731";
            var messages = new List<object> { new { role = "system", content = "Ты полезный помощник личного дашборда. Отвечай кратко и по существу. Не заявляй, что выполнил изменение, если не получил отдельную команду API." } };
            IReadOnlyList<ChatMessage> context = history is { Count: > 0 } ? history : [new ChatMessage(Guid.NewGuid(), "user", text, DateTimeOffset.UtcNow)];
            foreach (var message in context) messages.Add(new { role = message.Role == "agent" ? "assistant" : "user", content = message.Text });
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(new { model, temperature = 0.3, messages }), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            try { using var response = await _http.SendAsync(request, ct); if (response.IsSuccessStatusCode) { using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var answer = payload.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(); if (!string.IsNullOrWhiteSpace(answer)) return answer.Trim(); } }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Offline fallback below. */ }
        }
        return $"Вы спросили: {text.Trim()}\n\nЯ могу ответить на вопрос или помочь с изменениями в текущем разделе дашборда.";
    }

}

sealed class KnowledgeChatFacade(KnowledgeService knowledge, IChatResponder responder) : IChatConversationFacade
{
    public ChatScope Scope => ChatScope.Knowledge;
    public Task<ChatReply> HandleAsync(string text, bool initialPrompt, CancellationToken ct) => HandleAsync(new ChatTurn(text, initialPrompt, null, []), ct);

    public async Task<ChatReply> HandleAsync(ChatTurn turn, CancellationToken ct)
    {
        var plan = KnowledgeCommandPlanner.Continue(turn.Pending, turn.Text) ?? KnowledgeCommandPlanner.Plan(turn.Text);
        if (plan.Kind == KnowledgeCommandKind.None) return new ChatReply(await responder.ReplyAsync(turn.Text, turn.History, ct), false);
        if (plan.Missing is not null) return Clarify(plan, MissingMessage(plan.Missing));

        switch (plan.Kind)
        {
            case KnowledgeCommandKind.CreateDocument:
                var parent = plan.SectionTitle is null ? null : await FindSectionAsync(plan.SectionTitle, ct);
                if (plan.SectionTitle is not null && parent is null) return Clarify(plan with { SectionTitle = null, Missing = "section" }, $"Раздел «{plan.SectionTitle}» не найден. Укажите существующий раздел.");
                var created = await knowledge.CreateAsync("document", plan.Title!, plan.Content ?? "", parent, ct);
                return created.Node is null ? new ChatReply(created.Error ?? "Не удалось создать документ.", true) : new ChatReply($"Документ «{created.Node.Title}» создан.", false, true);
            case KnowledgeCommandKind.Rename:
                var rename = await knowledge.RenameAsync(plan.NodeId!.Value, plan.Title, ct);
                return rename is null ? new ChatReply("Название изменено.", false, true) : new ChatReply(rename, true);
            case KnowledgeCommandKind.UpdateContent:
                var content = await knowledge.UpdateContentAsync(plan.NodeId!.Value, plan.Content, ct);
                return content is null ? new ChatReply("Содержание документа обновлено.", false, true) : new ChatReply(content, true);
            case KnowledgeCommandKind.Move:
                var target = await FindSectionAsync(plan.SectionTitle!, ct);
                if (target is null) return Clarify(plan with { SectionTitle = null, Missing = "section" }, $"Раздел «{plan.SectionTitle}» не найден. Укажите существующий раздел.");
                var move = await knowledge.MoveAsync(plan.NodeId!.Value, target, 0, ct);
                return move is null ? new ChatReply("Документ перемещён.", false, true) : new ChatReply(move, true);
            default: return new ChatReply("Не удалось определить команду базы знаний.", true);
        }
    }

    private static ChatReply Clarify(KnowledgeCommand command, string message) => new(message, true, false, new ChatPending("knowledge-command", JsonSerializer.Serialize(command)));
    private static string MissingMessage(string missing) => missing switch { "title" => "Укажите название документа.", "section" => "Укажите существующий раздел.", "id" => "Укажите идентификатор документа.", "content" => "Укажите новое содержание документа.", _ => "Укажите недостающие данные команды." };
    private async Task<Guid?> FindSectionAsync(string title, CancellationToken ct) => (await knowledge.GetTreeAsync(ct)).SelectMany(Flatten).FirstOrDefault(x => x.Kind == "section" && x.Title.Equals(title, StringComparison.OrdinalIgnoreCase))?.Id;
    private static IEnumerable<KnowledgeNodeDto> Flatten(KnowledgeNodeDto node) { yield return node; if (node.Children is not null) foreach (var child in node.Children.SelectMany(Flatten)) yield return child; }
}

internal enum KnowledgeCommandKind { None, CreateDocument, Rename, UpdateContent, Move }
internal sealed record KnowledgeCommand(KnowledgeCommandKind Kind, Guid? NodeId = null, string? Title = null, string? Content = null, string? SectionTitle = null, string? Missing = null);

/// <summary>Conservative command planner: only imperatives mutate; structured pending state completes a prior command safely.</summary>
internal static class KnowledgeCommandPlanner
{
    public static KnowledgeCommand? Continue(ChatPending? pending, string answer)
    {
        if (pending?.Kind != "knowledge-command") return null;
        KnowledgeCommand? command;
        try { command = JsonSerializer.Deserialize<KnowledgeCommand>(pending.Data); } catch { return null; }
        if (command?.Missing is null) return null;
        var value = answer.Trim(); if (string.IsNullOrWhiteSpace(value)) return command;
        return command.Missing switch
        {
            "title" => command with { Title = value, Missing = null },
            "section" => command with { SectionTitle = value, Missing = null },
            "content" => command with { Content = value, Missing = null },
            "id" when TryId(value, out var id) => command with { NodeId = id, Missing = null },
            "id" => command,
            _ => null
        };
    }

    public static KnowledgeCommand Plan(string source)
    {
        var text = source.Trim(); var lower = text.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text) || text.EndsWith('?')) return new(KnowledgeCommandKind.None);
        if (StartsWithAny(lower, "создай документ", "создать документ", "добавь документ", "добавить документ")) return CreateDocument(text);
        if (StartsWithAny(lower, "переименуй", "переименовать")) return NamedCommand(text, KnowledgeCommandKind.Rename, "название");
        if (StartsWithAny(lower, "обнови содержание", "измени содержание", "обновить содержание", "изменить содержание")) return NamedCommand(text, KnowledgeCommandKind.UpdateContent, "содержание");
        if (StartsWithAny(lower, "перемести", "перенеси", "переместить", "перенести"))
        {
            if (!TryId(text, out var id)) return new(KnowledgeCommandKind.Move, Missing: "id");
            var section = ExtractAfterPhrase(text, "в разделе") ?? ExtractAfterPhrase(text, "в раздел");
            return new(KnowledgeCommandKind.Move, id, SectionTitle: section, Missing: string.IsNullOrWhiteSpace(section) ? "section" : null);
        }
        return new(KnowledgeCommandKind.None);
    }

    private static KnowledgeCommand CreateDocument(string text)
    {
        var lower = text.ToLowerInvariant();
        string[] commands = ["создай документ", "создать документ", "добавь документ", "добавить документ"];
        var command = commands.First(prefix => lower.Contains(prefix));
        var commandEnd = lower.IndexOf(command, StringComparison.Ordinal) + command.Length;
        var rest = text[commandEnd..].Trim(' ', ':', '-', '«', '»');
        var sectionMarker = FindPhrase(rest, "в разделе") ?? FindPhrase(rest, "в раздел");
        var contentMarker = FindPhrase(rest, "с содержанием") ?? FindPhrase(rest, "содержание") ?? FindPhrase(rest, "с текстом") ?? FindPhrase(rest, "текст");
        var titleEnd = new[] { sectionMarker?.Index, contentMarker?.Index }.Where(x => x is not null).Select(x => x!.Value).DefaultIfEmpty(rest.Length).Min();
        var title = TrimValue(rest[..titleEnd]);
        if (title is not null) title = RemovePrefix(title, "с названием") ?? RemovePrefix(title, "название") ?? title;
        var content = contentMarker is null ? null : TrimValue(rest[contentMarker.Value.End..(sectionMarker is { } section && section.Index > contentMarker.Value.Index ? section.Index : rest.Length)]);
        var sectionTitle = sectionMarker is null ? null : TrimValue(rest[sectionMarker.Value.End..]);
        return new(KnowledgeCommandKind.CreateDocument, Title: title, Content: content, SectionTitle: sectionTitle, Missing: string.IsNullOrWhiteSpace(title) ? "title" : null);
    }

    private static KnowledgeCommand NamedCommand(string text, KnowledgeCommandKind kind, string marker)
    {
        if (!TryId(text, out var id)) return new(kind, Missing: "id");
        var value = ExtractAfterPhrase(text, marker) ?? ExtractAfterPhrase(text, "на");
        return kind == KnowledgeCommandKind.Rename
            ? new(kind, id, Title: value, Missing: string.IsNullOrWhiteSpace(value) ? "title" : null)
            : new(kind, id, Content: value, Missing: string.IsNullOrWhiteSpace(value) ? "content" : null);
    }

    private static bool StartsWithAny(string value, params string[] prefixes) => prefixes.Any(value.StartsWith);
    private static (int Index, int End)? FindPhrase(string text, string phrase)
    { var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); return index < 0 ? null : (index, index + phrase.Length); }
    private static string? ExtractAfterPhrase(string text, string phrase) => FindPhrase(text, phrase) is { } marker ? TrimValue(text[marker.End..]) : null;
    private static string? RemovePrefix(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? TrimValue(text[prefix.Length..]) : null;
    private static string? TrimValue(string value) { var trimmed = value.Trim(' ', ':', '-', '«', '»', '.'); return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed; }
    private static bool TryId(string text, out Guid id) { foreach (var token in text.Split([' ', ',', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)) if (Guid.TryParse(token.Trim('«', '»', '.'), out id)) return true; id = default; return false; }
}
