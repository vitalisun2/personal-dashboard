using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;

namespace PersonalDashboard.V2.Tasks.Infrastructure;

public sealed class TasksAgentAccess(TasksService service) : ITasksAgentAccess
{
    public async Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken ct = default)
    {
        if (kind == TaskEntityKind.Task)
        {
            var item = await service.GetAsync(id, ct);
            return item is null ? null : State(item);
        }
        foreach (var bucket in new[] { TaskLocation.Backlog, TaskLocation.Today })
            if ((await service.SectionsAsync(bucket, ct)).FirstOrDefault(x => x.Id == id) is { } section) return SectionState(section);
        return null;
    }

    public async Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken ct = default)
    {
        try
        {
            if (mutation.Operation == TaskMutationKind.Create && await ReadAsync(mutation.Kind, mutation.Id, ct) is { } existing)
                return new(false, existing, "An entity with this ID already exists.");
            if (mutation.Kind == TaskEntityKind.Task)
            {
                if (mutation.Operation == TaskMutationKind.Delete)
                {
                    await service.DeleteAsync(mutation.Id, Version(mutation), ct);
                    return new(true, null, null);
                }
                var item = mutation.Operation switch
                {
                    TaskMutationKind.Create => await service.CreateAsync(mutation.Title ?? "", mutation.Description, mutation.Planning?.ProjectId, mutation.Planning?.MilestoneId, mutation.Planning?.FeatureId, mutation.SectionId, ct, mutation.Id),
                    TaskMutationKind.Update => await service.UpdateMutationAsync(mutation.Id, Version(mutation), mutation.Title, mutation.Description, mutation.SectionId, ct),
                    TaskMutationKind.Move => await Move(mutation, ct),
                    TaskMutationKind.SetWorkStatus => await service.SetStatusAsync(mutation.Id, Version(mutation), Parse<TaskWorkStatus>(mutation.WorkStatus), ct),
                    TaskMutationKind.Archive => await service.ArchiveAsync(mutation.Id, Version(mutation), ct),
                    TaskMutationKind.Restore => await service.RestoreAsync(mutation.Id, Version(mutation), mutation.SectionId, ct),
                    TaskMutationKind.Reorder => await ReorderTask(mutation, ct),
                    _ => throw new ArgumentException("Unsupported task operation.")
                };
                return new(true, State(item), null);
            }
            return await ApplySection(mutation, ct);
        }
        catch (Exception ex) when (ex is TaskVersionConflictException or KeyNotFoundException or ArgumentException or InvalidOperationException or InvalidPlanningLinkException)
        { return new(false, await ReadAsync(mutation.Kind, mutation.Id, ct), ex.Message); }
    }

    private async Task<TaskView> Move(TaskMutation m, CancellationToken ct) => m.Placement?.ToLowerInvariant() switch
    {
        "today" => await service.MoveToTodayAsync(m.Id, Version(m), ct),
        "backlog" => await service.MoveToBacklogAsync(m.Id, Version(m), ct),
        "planned" => await service.ReturnToPlanAsync(m.Id, Version(m), ct),
        _ => throw new ArgumentException("Placement must be planned, backlog or today.")
    };
    private async Task<TaskView> ReorderTask(TaskMutation m, CancellationToken ct)
    {
        if (m.Order is null) throw new ArgumentException("Reorder requires ordered IDs with expected versions.");
        TaskLocation location;
        Guid? projectId = null;
        Guid? milestoneId = null;
        Guid? featureId = null;
        if (string.Equals(m.Placement, "planned", StringComparison.OrdinalIgnoreCase))
        {
            location = TaskLocation.Planned;
            projectId = m.Planning?.ProjectId;
            milestoneId = m.Planning?.MilestoneId;
            featureId = m.Planning?.FeatureId;
        }
        else
        {
            location = m.Bucket?.ToLowerInvariant() switch
            {
                "today" => TaskLocation.Today,
                "backlog" => TaskLocation.Backlog,
                _ => throw new ArgumentException("Reorder requires a backlog or today bucket.")
            };
            projectId = m.Planning?.ProjectId;
        }
        var result = await service.ReorderTasksAsync(location, m.SectionId, m.Order.Select(x => new TaskOrderItem(x.Id, x.ExpectedVersion)).ToArray(), ct, projectId, milestoneId, featureId);
        return result.Single(x => x.Id == m.Id);
    }

    private async Task<TaskMutationResult> ApplySection(TaskMutation m, CancellationToken ct)
    {
        var bucket = m.Bucket is null ? throw new ArgumentException("Section bucket is required.") : m.Bucket.Equals("today", StringComparison.OrdinalIgnoreCase) ? TaskLocation.Today : m.Bucket.Equals("backlog", StringComparison.OrdinalIgnoreCase) ? TaskLocation.Backlog : throw new ArgumentException("Bucket must be backlog or today.");
        TaskSectionView? current = null;
        foreach (var location in new[] { TaskLocation.Backlog, TaskLocation.Today }) current ??= (await service.SectionsAsync(location, ct)).SingleOrDefault(s => s.Id == m.Id);
        if (m.Operation == TaskMutationKind.Create) current = await service.CreateSectionAsync(m.Title ?? "", bucket, ct, m.Id);
        else if (m.Operation == TaskMutationKind.Update) current = await service.RenameSectionAsync(m.Id, Version(m), m.Title ?? "", ct);
        else if (m.Operation == TaskMutationKind.Delete) { await service.DeleteSectionAsync(m.Id, Version(m), ct); return new(true, null, null); }
        else if (m.Operation == TaskMutationKind.Reorder && m.Order is not null)
        {
            var currentSections = await service.SectionsAsync(bucket, ct);
            if (m.Order.Count != currentSections.Count || m.Order.Any(x => currentSections.SingleOrDefault(s => s.Id == x.Id)?.Version != x.ExpectedVersion)) throw new ArgumentException("Section order is stale or incomplete.");
            var result = await service.ReorderSectionsAsync(bucket, currentSections.Count == 0 ? 0 : currentSections.Max(x => x.Version), m.Order.Select(x => x.Id).ToArray(), ct);
            current = result.FirstOrDefault(x => x.Id == m.Id);
        }
        else throw new ArgumentException("Unsupported section operation.");
        return new(true, SectionState(current), null);
    }
    private static long Version(TaskMutation m) => m.ExpectedVersion ?? throw new ArgumentException("ExpectedVersion is required.");
    private static T Parse<T>(string? value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var result) ? result : throw new ArgumentException($"Invalid {typeof(T).Name}.");
    private static TaskEntityState State(TaskView x) => new(TaskEntityKind.Task, x.Id, x.Version, x.Title, x.Description, x.ProjectId is null ? null : new(x.ProjectId, x.MilestoneId, x.FeatureId), x.Location.ToString().ToLowerInvariant(), x.WorkStatus.ToString().ToLowerInvariant(), x.SectionId, null, x.Position);
    private static TaskEntityState SectionState(TaskSectionView? x) => x is null ? null! : new(TaskEntityKind.Section, x.Id, x.Version, x.Name, null, null, null, null, null, x.Location == TaskLocation.Today ? "today" : "backlog", x.Position);
}

public sealed class TaskSyncMutationHandler(ITasksAgentAccess access) : ISyncMutationHandler
{
    public string Type => "tasks.task";
    public async Task<SyncMutationResult> ApplyAsync(SyncOperation operation, CancellationToken ct = default)
    {
        var before = await access.ReadAsync(TaskEntityKind.Task, operation.Id, ct);
        var (mutation, error) = ReadMutation(operation, TaskEntityKind.Task, Type);
        if (mutation is null) return new(false, before is null ? null : ToSnapshot(before), error);
        var result = await access.ApplyAsync(mutation, ct);
        var current = result.Current is not null ? ToSnapshot(result.Current) : result.Applied && operation.Kind == SyncOperationKind.Delete && before is not null ? new EntitySnapshot("tasks.task", operation.Id, before.Version + 1, true, null) : null;
        return new(result.Applied, current, result.ConflictReason);
    }
    internal static (TaskMutation? Mutation, string? Error) ReadMutation(SyncOperation operation, TaskEntityKind expectedKind, string expectedType)
    {
        if (!string.Equals(operation.Type, expectedType, StringComparison.Ordinal)) return (null, $"Expected entity type {expectedType}.");
        TaskMutation? mutation;
        try
        {
            mutation = operation.Payload is { } payload
                ? payload.Deserialize<TaskMutation>(MutationJsonOptions)
                : operation.Kind == SyncOperationKind.Delete
                    ? new(TaskMutationKind.Delete, expectedKind, operation.Id, operation.ExpectedVersion)
                    : null;
        }
        catch (JsonException ex) { return (null, $"Invalid task mutation payload: {ex.Message}"); }
        if (mutation is null) return (null, "Task mutation payload is required.");
        if (mutation.Kind != expectedKind) return (null, $"Expected entity kind {expectedKind}.");
        if (mutation.Id == Guid.Empty || mutation.Id != operation.Id) return (null, "Payload ID must match the sync operation ID.");
        if (operation.Kind == SyncOperationKind.Delete && mutation.Operation != TaskMutationKind.Delete) return (null, "Delete sync operations require a delete mutation.");
        if (operation.Kind == SyncOperationKind.Upsert && mutation.Operation == TaskMutationKind.Delete) return (null, "Delete mutations require a delete sync operation.");
        if (mutation.Operation == TaskMutationKind.Create)
        {
            if (operation.Kind != SyncOperationKind.Upsert || operation.ExpectedVersion is not null || mutation.ExpectedVersion is not null)
                return (null, "Create sync operations must not include an expectedVersion.");
        }
        else if (operation.ExpectedVersion is null)
        {
            return (null, "Non-create sync operations require an expectedVersion.");
        }
        if (operation.ExpectedVersion is { } expected && mutation.ExpectedVersion is { } payloadExpected && expected != payloadExpected)
            return (null, "Payload expectedVersion must match the sync operation expectedVersion.");
        return (mutation with { ExpectedVersion = operation.ExpectedVersion ?? mutation.ExpectedVersion }, null);
    }

    private static readonly JsonSerializerOptions MutationJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    internal static PersonalDashboard.V2.Contracts.Changes.EntitySnapshot ToSnapshot(TaskEntityState x) => new(x.Kind == TaskEntityKind.Task ? "tasks.task" : "tasks.section", x.Id, x.Version, false, JsonSerializer.SerializeToElement(new { title = x.Title, body = x.Description, projectId = x.Planning?.ProjectId, milestoneId = x.Planning?.MilestoneId, featureId = x.Planning?.FeatureId, placement = x.Placement, workStatus = x.WorkStatus, sectionId = x.SectionId, bucket = x.Bucket, position = x.Position, path = (string?)null, url = $"/tasks/{x.Id}" }));
}

public sealed class TaskSectionSyncMutationHandler(ITasksAgentAccess access) : ISyncMutationHandler
{
    public string Type => "tasks.section";
    public async Task<SyncMutationResult> ApplyAsync(SyncOperation operation, CancellationToken ct = default)
    {
        var before = await access.ReadAsync(TaskEntityKind.Section, operation.Id, ct);
        var (mutation, error) = TaskSyncMutationHandler.ReadMutation(operation, TaskEntityKind.Section, Type);
        if (mutation is null) return new(false, before is null ? null : TaskSyncMutationHandler.ToSnapshot(before), error);
        var result = await access.ApplyAsync(mutation, ct);
        var current = result.Current is not null ? TaskSyncMutationHandler.ToSnapshot(result.Current) : result.Applied && operation.Kind == SyncOperationKind.Delete && before is not null ? new EntitySnapshot("tasks.section", operation.Id, before.Version + 1, true, null) : null;
        return new(result.Applied, current, result.ConflictReason);
    }
}
