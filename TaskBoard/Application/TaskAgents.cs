using System.Text.Json;
using Microsoft.Extensions.Logging;
using TaskBoard.Domain;

namespace TaskBoard.Application;

public static class TaskPrompt
{
    public const string Instructions =
        "Ты — помощник личного дашборда задач. Из надиктованного пользователем текста задачи выдели: " +
        "короткий заголовок (2-6 слов, без точки в конце); лаконичное описание (1-3 коротких предложения, " +
        "кратко и по делу: только суть и факты исходного текста, ничего не теряя и ничего не добавляя; " +
        "без канцелярита, приветствий и лишних деталей); раздел — выбери один из СПИСКА существующих разделов, " +
        "если текст явно про него; раздел «Общее» используй ТОЛЬКО если ни один из существующих разделов не подходит; " +
        "если подходящего раздела в списке нет — придумай новое короткое название (2-4 слова). " +
        "Верни строго JSON-объект вида {\"title\": \"...\", \"description\": \"...\", \"section\": \"...\"} без markdown и лишнего текста.";

    public static string BuildSystemPrompt(IReadOnlyCollection<string> existingSections) => Instructions + "\n\nСуществующие разделы: " + string.Join("; ", existingSections);
    public static string BuildRevisionRequest(TaskDraft draft, string correction) =>
        "Обнови черновик задачи по правке пользователя. Сохрани все детали, которые правка не отменяет. Если правка просит перенести задачу в другой раздел или создать новый — обнови раздел. Описание держи кратким и лаконичным: только суть и факты, без выдуманных деталей. Верни только итоговый JSON по системной инструкции.\n\nТекущий черновик:\n" + Serialize(draft) + "\n\nПравка пользователя:\n" + correction;
    public static string BuildEditRequest(TaskDraft current, string instruction) =>
        "Обнови существующую задачу по указанию пользователя. Правка может касаться заголовка, описания или раздела. Применяй только запрошенные изменения: не меняй заголовок, если пользователь просит изменить только описание, и наоборот. Если правка просит перенести задачу в другой раздел — выбери его из списка существующих разделов; если названного раздела нет — создай новое короткое название (2-4 слова). Описание держи кратким и лаконичным: только суть и факты, без выдуманных деталей. Верни только итоговый JSON по системной инструкции.\n\nТекущая задача:\n" + Serialize(current) + "\n\nУказание пользователя:\n" + instruction;
    public static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"section\":{\"type\":\"string\"}},\"required\":[\"title\",\"description\",\"section\"],\"additionalProperties\":false}").RootElement.Clone();
    private static string Serialize(TaskDraft draft) => JsonSerializer.Serialize(draft, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>Preserves the existing OpenRouter → Ollama → deterministic local fallback cascade.</summary>
public sealed class LlmTaskAgent : ITaskAgent
{
    private readonly ILlmProvider[] _providers;
    private readonly ITaskAgent _fallback;
    private readonly ILogger<LlmTaskAgent> _logger;
    public LlmTaskAgent(ILogger<LlmTaskAgent> logger) : this([new OpenRouterClient(), new OllamaClient()], new LocalTaskAgent(), logger) { }
    internal LlmTaskAgent(ILlmProvider[] providers, ITaskAgent fallback, ILogger<LlmTaskAgent> logger) { _providers = providers; _fallback = fallback; _logger = logger; }

    public async Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> sections)
    {
        foreach (var provider in _providers) try { if (await provider.TryParseAsync(rawText, sections) is { } draft) { if (draft.Section.Equals("Общее", StringComparison.OrdinalIgnoreCase) && !LocalTaskAgent.FallbackSection(rawText, sections).Equals("Общее", StringComparison.OrdinalIgnoreCase)) draft = draft with { Section = LocalTaskAgent.FallbackSection(rawText, sections) }; _logger.LogInformation("Задача разобрана провайдером {Provider}", provider.Name); return draft; } } catch (Exception ex) { _logger.LogWarning(ex, "Провайдер {Provider} упал", provider.Name); }
        return await _fallback.CreateDraftAsync(rawText, sections);
    }
    public async Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> sections) => await ParseOrFallbackAsync(TaskPrompt.BuildRevisionRequest(draft, correction), sections, () => _fallback.ReviseDraftAsync(draft, correction, sections));
    public async Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> sections)
    {
        foreach (var provider in _providers) try { if (await provider.TryParseAsync(TaskPrompt.BuildEditRequest(current, instruction), sections) is { } edited) { if (edited.Section.Equals("Общее", StringComparison.OrdinalIgnoreCase) && !current.Section.Equals("Общее", StringComparison.OrdinalIgnoreCase) && !LocalTaskAgent.MentionsSectionMove(instruction, sections)) edited = edited with { Section = current.Section }; return edited; } } catch (Exception ex) { _logger.LogWarning(ex, "Провайдер {Provider} не обработал правку", provider.Name); }
        return await _fallback.EditDraftAsync(current, instruction, sections);
    }
    public async Task<string> ChatAsync(string text, IReadOnlyList<TaskConversationMessage>? history = null)
    {
        var context = history is { Count: > 0 } ? history : [new TaskConversationMessage("user", text)];
        foreach (var provider in _providers) try { if (await provider.TryChatAsync(context) is { Length: > 0 } reply) return reply.Trim(); } catch (Exception ex) { _logger.LogWarning(ex, "Провайдер {Provider} не обработал чат", provider.Name); }
        return await _fallback.ChatAsync(text, context);
    }
    private async Task<TaskDraft> ParseOrFallbackAsync(string text, IReadOnlyCollection<string> sections, Func<Task<TaskDraft>> fallback)
    { foreach (var provider in _providers) try { if (await provider.TryParseAsync(text, sections) is { } draft) return draft; } catch (Exception ex) { _logger.LogWarning(ex, "Провайдер {Provider} недоступен", provider.Name); } return await fallback(); }
}

public sealed class LocalTaskAgent : ITaskAgent
{
    public Task<string> ChatAsync(string text, IReadOnlyList<TaskConversationMessage>? history = null) => Task.FromResult($"Вы спросили: {text.Trim()}\n\nЯ могу помочь разобраться с этим и выполнить только изменения в задачнике: создать, отредактировать или перенести задачу.");
    public Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> sections) { var normalized = Normalize(rawText); var end = normalized.IndexOfAny(['.', '!', '?']); var title = end > 0 ? normalized[..end] : normalized; if (title.Length > 72) title = title[..69].TrimEnd() + "…"; return Task.FromResult(new TaskDraft(title, normalized, PickSection(normalized, sections))); }
    public Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> sections) { var note = Normalize(correction); return Task.FromResult(draft with { Title = TryExtractExplicitTitle(note) ?? draft.Title, Description = $"{draft.Description}\n\nУточнение: {note}" }); }
    public Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> sections) { var note = Normalize(instruction); var title = TryExtractExplicitTitle(note) ?? current.Title; var section = TryExtractSection(note, sections); var description = title == current.Title && section is null ? current.Description + "\n\nУточнение: " + note : current.Description; return Task.FromResult(new TaskDraft(title, description, section ?? current.Section)); }
    public static bool MentionsSectionMove(string instruction, IReadOnlyCollection<string> sections) { var lower = instruction.ToLowerInvariant(); return new[] { "раздел", "секци", "перенес", "перемест", "переведи" }.Any(lower.Contains) || sections.Any(section => section.Length > 2 && lower.Contains(section.ToLowerInvariant())); }
    public static string FallbackSection(string text, IReadOnlyCollection<string> sections) => PickSection(text, sections);
    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string? TryExtractExplicitTitle(string correction) { var lower = correction.ToLowerInvariant(); var marker = lower.IndexOf("заголовок", StringComparison.Ordinal); if (marker < 0) marker = lower.IndexOf("название", StringComparison.Ordinal); if (marker < 0) return null; var length = lower[marker..].StartsWith("заголовок", StringComparison.Ordinal) ? 9 : 8; var tail = correction[(marker + length)..].Trim(); if (tail.StartsWith("на", StringComparison.OrdinalIgnoreCase)) tail = tail[2..].TrimStart(' ', ':', '-'); else tail = tail.TrimStart(' ', ':', '-'); var end = tail.IndexOfAny(['.', ';', '\n']); if (end >= 0) tail = tail[..end].TrimEnd(); return tail.Length is > 0 and <= 120 ? tail : null; }
    private static string? TryExtractSection(string note, IReadOnlyCollection<string> sections) { var lower = note.ToLowerInvariant(); var marker = lower.IndexOf("раздел", StringComparison.Ordinal); var length = 6; if (marker < 0) { marker = lower.IndexOf("секци", StringComparison.Ordinal); length = 5; } if (marker < 0) return null; var tail = note[(marker + length)..].TrimStart(' ', ':', '-'); if (tail.StartsWith("на", StringComparison.OrdinalIgnoreCase)) tail = tail[2..].TrimStart(' ', ':', '-'); else if (tail.StartsWith("в ", StringComparison.OrdinalIgnoreCase)) tail = tail[2..].TrimStart(' ', ':', '-'); var cut = tail.IndexOfAny(['.', ';', '\n', '!', '?']); if (cut < 0) cut = tail.IndexOf(" на ", StringComparison.OrdinalIgnoreCase); if (cut < 0) cut = tail.IndexOf(" чтобы", StringComparison.OrdinalIgnoreCase); if (cut < 0) cut = tail.IndexOf(" и ", StringComparison.OrdinalIgnoreCase); if (cut >= 0) tail = tail[..cut].TrimEnd(); if (tail.Length == 0) return null; if (tail.Length > 40) tail = tail[..40].TrimEnd(); return sections.FirstOrDefault(s => s.Equals(tail, StringComparison.OrdinalIgnoreCase)) ?? tail; }
    private static string PickSection(string text, IReadOnlyCollection<string> existingSections) { var sections = existingSections.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (sections.Where(section => text.Contains(section, StringComparison.OrdinalIgnoreCase)).OrderByDescending(section => section.Length).FirstOrDefault() is { } exact) return exact; var lower = text.ToLowerInvariant(); var hints = new Dictionary<string, string[]> { ["Личный дашборд"] = ["дашборд", "dashboard", "памят", "интерфейс", "личное приложение"], ["Lost Cyber Hamster"] = ["hamster", "хомяк", "lost cyber", "lch", "unity", "париж", "барселон", "квест", "energy bar", "прыж"], ["Workflow"] = ["workflow", "агент", "инструкц", "prompt", "промпт", "оркестратор", "hindsight", "graphify"] }; foreach (var (suggested, keywords) in hints) if (keywords.Any(lower.Contains) && sections.FirstOrDefault(x => x.Equals(suggested, StringComparison.OrdinalIgnoreCase)) is { } match) return match; return sections.FirstOrDefault(x => x.Equals("Общее", StringComparison.OrdinalIgnoreCase)) ?? "Общее"; }
}
