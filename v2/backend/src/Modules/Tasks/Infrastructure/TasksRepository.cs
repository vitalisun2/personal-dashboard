using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Tasks.Infrastructure.Persistence;

namespace PersonalDashboard.V2.Tasks.Infrastructure;

public sealed class TasksRepository(PlatformDbContext db, ITransactionRunner transaction, IEntityChangeJournal journal, PersonalDashboard.V2.Contracts.Planning.IPlanningPathReader paths) : ITasksRepository
{
    public async Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct) =>
        await db.Set<TaskItem>().AsNoTracking().Where(x => location == null || x.Location == location).OrderBy(x => x.Position).ToListAsync(ct);

    public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct) => db.Set<TaskItem>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task SaveAsync(TaskItem item, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        if (db.Entry(item).State == EntityState.Detached)
        {
            if (item.Version > 1)
            {
                db.Attach(item);
                db.Entry(item).State = EntityState.Modified;
                db.Entry(item).Property(x => x.Version).OriginalValue = item.Version - 1;
            }
            else
            {
                if (await db.Set<TaskTombstone>().AnyAsync(x => x.Id == item.Id, token))
                    throw new InvalidOperationException($"Task ID {item.Id} has already been used.");
                db.Add(item);
            }
        }
        var path = item.ProjectId is { } projectId ? await paths.ReadPathAsync(new(projectId, item.MilestoneId, item.FeatureId), token) : null;
        var payload = Payload(item, path?.Path);
        await journal.AppendAsync(new EntitySnapshot("tasks.task", item.Id, item.Version, false, JsonSerializer.SerializeToElement(payload)), token);
    }, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            var item = await GetAsync(id, token) ?? throw new KeyNotFoundException($"Task {id} was not found.");
            db.Remove(item);
            var tombstone = await db.Set<TaskTombstone>().SingleOrDefaultAsync(x => x.Id == id, token);
            if (tombstone is null)
                db.Add(new TaskTombstone { Id = item.Id, Version = item.Version + 1, UpdatedAtUtc = DateTimeOffset.UtcNow });
            else
            {
                tombstone.Version = item.Version + 1;
                tombstone.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            await journal.AppendAsync(new EntitySnapshot("tasks.task", item.Id, item.Version + 1, true, null), token);
        }, ct);
    }

    public async Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct) =>
        await db.Set<TaskSection>().AsNoTracking().Where(x => x.Location == location).OrderBy(x => x.Position).ToListAsync(ct);

    public Task SaveSectionAsync(TaskSection section, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        if (db.Entry(section).State == EntityState.Detached)
        {
            if (section.Version > 1)
            {
                db.Attach(section);
                db.Entry(section).State = EntityState.Modified;
                db.Entry(section).Property(x => x.Version).OriginalValue = section.Version - 1;
            }
            else db.Add(section);
        }
        await journal.AppendAsync(new EntitySnapshot("tasks.section", section.Id, section.Version, false, JsonSerializer.SerializeToElement(new { title = section.Name, bucket = Bucket(section.Location), position = section.Position, url = "/tasks", updatedAtUtc = DateTimeOffset.UtcNow })), token);
    }, ct);

    public async Task DeleteSectionAsync(Guid id, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            var section = await db.Set<TaskSection>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException($"Section {id} was not found.");
            if (await db.Set<TaskItem>().AnyAsync(x => x.SectionId == id, token)) throw new InvalidOperationException("Move the section's tasks before deleting it.");
            db.Remove(section);
            await journal.AppendAsync(new EntitySnapshot("tasks.section", section.Id, section.Version + 1, true, null), token);
        }, ct);
    }

    internal static object PayloadForProjection(TaskItem task, string? path) => Payload(task, path);
    private static object Payload(TaskItem task, string? path) => new
    {
        title = task.Title,
        body = task.Description,
        projectId = task.ProjectId,
        milestoneId = task.MilestoneId,
        featureId = task.FeatureId,
        placement = Placement(task.Location),
        workStatus = task.WorkStatus.ToString().ToLowerInvariant(),
        sectionId = task.SectionId,
        position = task.Position,
        path,
        url = $"/tasks/{task.Id}",
        updatedAtUtc = task.UpdatedAtUtc
    };

    private static string Placement(TaskLocation value) => value switch { TaskLocation.Planned => "planned", TaskLocation.Backlog => "backlog", TaskLocation.Today => "today", _ => "archived" };
    private static string Bucket(TaskLocation value) => value == TaskLocation.Today ? "today" : "backlog";
}

public sealed class TaskPlanningLinkUsage(PlatformDbContext db) : PersonalDashboard.V2.Contracts.Planning.ITaskPlanningLinkUsage
{
    public Task<bool> IsInUseAsync(PersonalDashboard.V2.Contracts.Planning.PlanningLink link, CancellationToken ct = default)
    {
        var tasks = db.Set<TaskItem>().AsNoTracking();
        if (link.FeatureId is { } featureId) return tasks.AnyAsync(x => x.FeatureId == featureId, ct);
        if (link.MilestoneId is { } milestoneId) return tasks.AnyAsync(x => x.MilestoneId == milestoneId, ct);
        if (link.ProjectId is { } projectId) return tasks.AnyAsync(x => x.ProjectId == projectId, ct);
        return Task.FromResult(false);
    }
}

public sealed class TaskPlanningProjectionRefresh(PlatformDbContext db, IEntityChangeJournal journal, ITransactionRunner transaction, PersonalDashboard.V2.Contracts.Planning.IPlanningPathReader paths) : PersonalDashboard.V2.Contracts.Planning.ITaskPlanningProjectionRefresh
{
    public Task RefreshAsync(PersonalDashboard.V2.Contracts.Planning.PlanningLink changedAncestor, CancellationToken ct = default) => transaction.ExecuteAsync(async token =>
    {
        var query = db.Set<TaskItem>().Where(x => changedAncestor.FeatureId != null ? x.FeatureId == changedAncestor.FeatureId : changedAncestor.MilestoneId != null ? x.MilestoneId == changedAncestor.MilestoneId : x.ProjectId == changedAncestor.ProjectId);
        var items = await query.ToListAsync(token);
        foreach (var item in items)
        {
            item.RefreshPlanningProjection();
            var path = await paths.ReadPathAsync(new(item.ProjectId, item.MilestoneId, item.FeatureId), token);
            await journal.AppendAsync(new EntitySnapshot("tasks.task", item.Id, item.Version, false, JsonSerializer.SerializeToElement(TasksRepository.PayloadForProjection(item, path?.Path))), token);
        }
    }, ct);
}
