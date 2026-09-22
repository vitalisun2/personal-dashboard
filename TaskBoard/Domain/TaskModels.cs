namespace TaskBoard.Domain;

public enum TaskBucket { Backlog, Today }
public enum TaskStatus { New, InProgress, Completed }
public sealed record TaskItem(Guid Id, string Title, string Description, string Section, TaskBucket Bucket, TaskStatus Status, DateTimeOffset CreatedAt);
public sealed record TaskDraft(string Title, string Description, string Section);

public sealed record CreateTaskRequest(string? Text);
public sealed record CreateTaskDraftRequest(string? Text);
public sealed record ReviseTaskDraftRequest(TaskDraft? Draft, string? Correction);
public sealed record ConfirmTaskDraftRequest(TaskDraft? Draft);
public sealed record UpdateDescriptionRequest(string? Description);
public sealed record UpdateTitleRequest(string? Title);
public sealed record UpdateSectionRequest(string? Section);
public sealed record EditTaskRequest(string? Text);
public sealed record MoveTaskRequest(TaskBucket Bucket);
public sealed record RenameSectionRequest(string? OldName, string? NewName);
public sealed record ReorderTasksRequest(TaskBucket Bucket, string? Section, IReadOnlyList<Guid>? TaskIds);
public sealed record ReorderSectionsRequest(TaskBucket Bucket, IReadOnlyList<string>? Sections);
