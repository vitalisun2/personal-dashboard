using TaskBoard.Domain;

namespace TaskBoard.Application;

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(TaskItem item, CancellationToken cancellationToken = default);
    Task<TaskItem?> UpdateAsync(Guid id, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken = default);
}

public interface ITaskAgent
{
    Task<string> ChatAsync(string text, IReadOnlyList<TaskConversationMessage>? history = null);
    Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections);
    Task<TaskDraft> ReviseDraftAsync(TaskDraft draft, string correction, IReadOnlyCollection<string> existingSections);
    Task<TaskDraft> EditDraftAsync(TaskDraft current, string instruction, IReadOnlyCollection<string> existingSections);
    Task<string?> ResolveChatActionAsync(IReadOnlyList<TaskConversationMessage> context) => Task.FromResult<string?>(null);
}

public interface IModelSelectableTaskAgent
{
    Task<string?> ResolveChatActionAsync(IReadOnlyList<TaskConversationMessage> context, bool gemmaOnly);
    Task<TaskDraft> CreateDraftAsync(string rawText, IReadOnlyCollection<string> existingSections, bool gemmaOnly);
    Task<string> ChatAsync(string text, IReadOnlyList<TaskConversationMessage>? history, bool gemmaOnly);
}

public sealed class ChatModelUnavailableException(string message) : Exception(message);

public sealed record TaskConversationMessage(string Role, string Text);
