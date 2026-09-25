using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Contracts.Planning;

namespace PersonalDashboard.V2.Tasks.Application;

public sealed record TaskView(Guid Id, string Title, string Description, Guid? ProjectId, Guid? MilestoneId, Guid? FeatureId, TaskLocation Location, TaskWorkStatus WorkStatus, Guid? SectionId, int Position, long Version);
public sealed record TaskSectionView(Guid Id, string Name, TaskLocation Location, int Position, long Version);
public sealed record TaskOrderItem(Guid Id, long ExpectedVersion);

public interface ITasksRepository
{
    Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct);
    Task<TaskItem?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(TaskItem item, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct);
    Task SaveSectionAsync(TaskSection section, CancellationToken ct);
    Task DeleteSectionAsync(Guid id, CancellationToken ct);
}

public sealed class TasksService(ITasksRepository repository, PersonalDashboard.V2.Contracts.Planning.IPlanningLinkValidator planning, PersonalDashboard.V2.Contracts.Transactions.ITransactionRunner transaction)
{
    public async Task<IReadOnlyList<TaskView>> ListAsync(TaskLocation? location, CancellationToken ct) => (await repository.ListAsync(location, ct)).OrderBy(t => t.Position).Select(Map).ToArray();
    public async Task<TaskView?> GetAsync(Guid id, CancellationToken ct) => await repository.GetAsync(id, ct) is { } item ? Map(item) : null;

    public async Task<TaskView> CreateAsync(string title, string? description, Guid? projectId, Guid? milestoneId, Guid? featureId, Guid? sectionId, CancellationToken ct, Guid? id = null)
    {
        var validation = await planning.ValidateAsync(new PlanningLink(projectId, milestoneId, featureId), ct);
        if (!validation.IsValid) throw new InvalidPlanningLinkException();
        if (projectId is null) sectionId = await ResolveSection(TaskLocation.Backlog, sectionId, null, ct);
        else sectionId = null;
        var item = new TaskItem(title, description, projectId, milestoneId, featureId, sectionId, id); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<TaskView> EditAsync(Guid id, long expectedVersion, string title, string? description, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion); item.Edit(title, description); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<TaskView> UpdateMutationAsync(Guid id, long expectedVersion, string? title, string? description, Guid? sectionId, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        if (sectionId is not null)
        {
            if (item.ProjectId is not null || item.Location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new InvalidOperationException("Only standalone Backlog or Today tasks have editable sections.");
            var section = await FindSection(sectionId.Value, ct);
            if (section.Location != item.Location) throw new ArgumentException("Task section must match its current bucket.");
        }
        item.Update(title, description, sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<TaskView> SetSectionAsync(Guid id, long expectedVersion, Guid sectionId, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        if (item.ProjectId is not null || item.Location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new InvalidOperationException("Only standalone Backlog or Today tasks have editable sections.");
        var section = await FindSection(sectionId, ct); if (section.Location != item.Location) throw new ArgumentException("Task section must match its current bucket.");
        item.SetSection(sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<IReadOnlyList<TaskView>> ReorderTasksAsync(TaskLocation location, Guid? sectionId, IReadOnlyList<TaskOrderItem> order, CancellationToken ct, Guid? projectId = null, Guid? milestoneId = null, Guid? featureId = null)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            if (location is not (TaskLocation.Planned or TaskLocation.Backlog or TaskLocation.Today)) throw new ArgumentException("Only planned, Backlog or Today tasks can be reordered.");
            IReadOnlyList<TaskItem> candidates = await repository.ListAsync(location, token);
            TaskItem[] current;
            if (location == TaskLocation.Planned)
            {
                if (featureId is null || sectionId is not null || projectId is null || milestoneId is null) throw new ArgumentException("Planned task order requires project, milestone and feature scope.");
                current = candidates.Where(t => t.ProjectId == projectId && t.MilestoneId == milestoneId && t.FeatureId == featureId).ToArray();
            }
            else if (projectId is not null)
            {
                if (sectionId is not null || milestoneId is not null || featureId is not null) throw new ArgumentException("Linked Backlog or Today task order is scoped to one project.");
                current = candidates.Where(t => t.ProjectId == projectId).ToArray();
            }
            else
            {
                if (sectionId is null || milestoneId is not null || featureId is not null) throw new ArgumentException("Standalone task order requires a section, or a linked order requires a project.");
                current = candidates.Where(t => t.ProjectId is null && t.SectionId == sectionId).ToArray();
            }
            if (order.Count != current.Length || order.Select(x => x.Id).Distinct().Count() != order.Count || !order.Select(x => x.Id).ToHashSet().SetEquals(current.Select(x => x.Id))) throw new ArgumentException("Order must include every current task in this scope exactly once.");
            foreach (var (entry, position) in order.Select((entry, position) => (entry, position)))
            {
                var task = current.Single(x => x.Id == entry.Id); Check(task.Version, entry.ExpectedVersion); task.SetPosition(position); await repository.SaveAsync(task, token);
            }
            return order.Select(entry => Map(current.Single(x => x.Id == entry.Id))).ToArray();
        }, ct);
    }

    public async Task<TaskView> MoveToTodayAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion); Guid? sectionId = item.ProjectId is null ? await ResolveSection(TaskLocation.Today, null, item.SectionId, ct) : null; item.MoveToToday(sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }
    public async Task<TaskView> MoveToBacklogAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion); Guid? sectionId = item.ProjectId is null ? await ResolveSection(TaskLocation.Backlog, null, item.SectionId, ct) : null; item.MoveToBacklog(sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }
    public Task<TaskView> ReturnToPlanAsync(Guid id, long expectedVersion, CancellationToken ct) => Mutate(id, expectedVersion, x => x.ReturnToPlan(), ct);
    public Task<TaskView> SetStatusAsync(Guid id, long expectedVersion, TaskWorkStatus status, CancellationToken ct) => Mutate(id, expectedVersion, x => x.SetWorkStatus(status), ct);
    public Task<TaskView> ArchiveAsync(Guid id, long expectedVersion, CancellationToken ct) => Mutate(id, expectedVersion, x => x.Archive(), ct);
    public async Task<TaskView> RestoreAsync(Guid id, long expectedVersion, Guid? sectionId, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        if (item.ProjectId is null) sectionId = await ResolveSection(TaskLocation.Backlog, sectionId, item.SectionId, ct);
        item.Restore(sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task DeleteAsync(Guid id, long expectedVersion, CancellationToken ct) { var item = await Required(id, ct); Check(item.Version, expectedVersion); await repository.DeleteAsync(id, ct); }

    public async Task<IReadOnlyList<TaskSectionView>> SectionsAsync(TaskLocation location, CancellationToken ct)
    {
        EnsureSectionLocation(location); return (await repository.ListSectionsAsync(location, ct)).OrderBy(s => s.Position).Select(Map).ToArray();
    }

    public async Task<TaskSectionView> CreateSectionAsync(string name, TaskLocation location, CancellationToken ct, Guid? id = null)
    {
        EnsureSectionLocation(location); var sections = await repository.ListSectionsAsync(location, ct); var section = new TaskSection(name, location, id, sections.Count); await repository.SaveSectionAsync(section, ct); return Map(section);
    }

    public async Task<TaskSectionView> RenameSectionAsync(Guid id, long expectedVersion, string name, CancellationToken ct)
    {
        var section = await FindSection(id, ct); Check(section.Version, expectedVersion); section.Rename(name); await repository.SaveSectionAsync(section, ct); return Map(section);
    }

    public async Task<IReadOnlyList<TaskSectionView>> ReorderSectionsAsync(TaskLocation location, long expectedVersion, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            EnsureSectionLocation(location); var list = (await repository.ListSectionsAsync(location, token)).OrderBy(s => s.Position).ToList(); Check(list.Count == 0 ? 0 : list.Max(s => s.Version), expectedVersion);
            if (ids.Count != list.Count || ids.Distinct().Count() != ids.Count || !ids.ToHashSet().SetEquals(list.Select(s => s.Id))) throw new ArgumentException("Order must include every current section exactly once.");
            for (var i = 0; i < ids.Count; i++) { var section = list.Single(x => x.Id == ids[i]); section.Reorder(i); await repository.SaveSectionAsync(section, token); }
            return await SectionsAsync(location, token);
        }, ct);
    }

    public async Task DeleteSectionAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var section = await FindSection(id, ct); Check(section.Version, expectedVersion);
        if ((await repository.ListAsync(section.Location, ct)).Any(task => task.SectionId == id)) throw new InvalidOperationException("Move the section's tasks before deleting it.");
        await repository.DeleteSectionAsync(id, ct);
    }

    private async Task<TaskView> Mutate(Guid id, long version, Action<TaskItem> action, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, version); action(item); await repository.SaveAsync(item, ct); return Map(item);
    }
    private async Task<TaskItem> Required(Guid id, CancellationToken ct) => await repository.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Task {id} was not found.");
    private async Task<TaskSection> FindSection(Guid id, CancellationToken ct) { foreach (var location in new[] { TaskLocation.Backlog, TaskLocation.Today }) { var section = (await repository.ListSectionsAsync(location, ct)).SingleOrDefault(x => x.Id == id); if (section is not null) return section; } throw new KeyNotFoundException($"Section {id} was not found."); }
    private async Task<Guid> ResolveSection(TaskLocation destination, Guid? requestedId, Guid? previousId, CancellationToken ct)
    {
        var sections = (await repository.ListSectionsAsync(destination, ct)).OrderBy(x => x.Position).ToArray();
        if (requestedId is { } wanted) return sections.SingleOrDefault(x => x.Id == wanted)?.Id ?? throw new ArgumentException("Section does not belong to the destination bucket.");
        string? previousName = null;
        if (previousId is { } oldId) { try { previousName = (await FindSection(oldId, ct)).Name; } catch (KeyNotFoundException) { } }
        if (previousName is not null && sections.FirstOrDefault(x => x.Name == previousName) is { } match) return match.Id;
        if (sections.FirstOrDefault() is { } first) return first.Id;
        return (await CreateSectionAsync(previousName ?? "Общее", destination, ct)).Id;
    }
    private static void Check(long actual, long expected) { if (actual != expected) throw new TaskVersionConflictException(actual, expected); }
    private static void EnsureSectionLocation(TaskLocation location) { if (location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new ArgumentException("Sections belong to Backlog or Today."); }
    private static TaskView Map(TaskItem x) => new(x.Id, x.Title, x.Description, x.ProjectId, x.MilestoneId, x.FeatureId, x.Location, x.WorkStatus, x.SectionId, x.Position, x.Version);
    private static TaskSectionView Map(TaskSection x) => new(x.Id, x.Name, x.Location, x.Position, x.Version);
}

public sealed class InvalidPlanningLinkException() : Exception("The project, milestone and feature link is invalid.");
public sealed class TaskVersionConflictException(long actual, long expected) : Exception($"Expected version {expected}, current version is {actual}.") { public long ActualVersion { get; } = actual; public long ExpectedVersion { get; } = expected; }
