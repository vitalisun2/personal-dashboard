using PersonalDashboard.V2.Contracts.Planning;

namespace PersonalDashboard.V2.Contracts.AgentAccess;

// Every member affected by a reorder is checked again at confirmation time.
public sealed record VersionedEntityId(Guid Id, long ExpectedVersion);

public enum PlanningEntityKind { Project, Milestone, Feature }
public enum PlanningMutationKind { Create, Update, Archive, Restore, Delete, SetFeatureCompletion, Reorder }

public sealed record PlanningEntityState(
    PlanningEntityKind Kind,
    Guid Id,
    Guid? ProjectId,
    Guid? MilestoneId,
    long Version,
    string Title,
    string? Description,
    bool? FeatureCompleted,
    bool Archived,
    int Position);

public sealed record PlanningMutation(
    PlanningMutationKind Operation,
    PlanningEntityKind Kind,
    Guid Id,
    Guid? ProjectId,
    Guid? MilestoneId,
    long? ExpectedVersion,
    string? Title = null,
    string? Description = null,
    bool? FeatureCompleted = null,
    IReadOnlyList<VersionedEntityId>? Order = null);

public sealed record PlanningMutationResult(bool Applied, PlanningEntityState? Current, string? ConflictReason);

public interface IPlanningAgentAccess
{
    Task<PlanningEntityState?> ReadAsync(
        PlanningEntityKind kind,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<PlanningMutationResult> ApplyAsync(
        PlanningMutation mutation,
        CancellationToken cancellationToken = default);
}

public enum TaskEntityKind { Task, Section }
public enum TaskMutationKind { Create, Update, Move, SetWorkStatus, Archive, Restore, Delete, Reorder }

public sealed record TaskEntityState(
    TaskEntityKind Kind,
    Guid Id,
    long Version,
    string Title,
    string? Description,
    PlanningLink? Planning,
    string? Placement,
    string? WorkStatus,
    Guid? SectionId,
    string? Bucket,
    int Position);

public sealed record TaskMutation(
    TaskMutationKind Operation,
    TaskEntityKind Kind,
    Guid Id,
    long? ExpectedVersion,
    string? Title = null,
    string? Description = null,
    PlanningLink? Planning = null,
    string? Placement = null,
    string? WorkStatus = null,
    Guid? SectionId = null,
    string? Bucket = null,
    IReadOnlyList<VersionedEntityId>? Order = null);

public sealed record TaskMutationResult(bool Applied, TaskEntityState? Current, string? ConflictReason);

public interface ITasksAgentAccess
{
    Task<TaskEntityState?> ReadAsync(
        TaskEntityKind kind,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<TaskMutationResult> ApplyAsync(
        TaskMutation mutation,
        CancellationToken cancellationToken = default);
}
