using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Planning.Application;
using PersonalDashboard.V2.Planning.Domain;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Planning.Infrastructure.Persistence;
using System.Text.Json;

namespace PersonalDashboard.V2.Planning.Infrastructure;

public sealed class PlanningRepository(PlatformDbContext db, ITransactionRunner transaction, IEntityChangeJournal journal) : IPlanningRepository
{
    public async Task<IReadOnlyList<Project>> ListProjectsAsync(bool includeArchived, CancellationToken ct) =>
        await db.Set<Project>().AsNoTracking().Where(x => includeArchived || !x.IsArchived).OrderBy(x => x.Position)
            .Include(x => x.Milestones).ThenInclude(x => x.Features).ToListAsync(ct);

    public Task<Project?> GetProjectAsync(Guid id, CancellationToken ct) =>
        db.Set<Project>().Include(x => x.Milestones).ThenInclude(x => x.Features).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<bool> EntityIdExistsAsync(Guid id, CancellationToken ct) =>
        await db.Set<Project>().AnyAsync(x => x.Id == id, ct) || await db.Set<OrderedEntity>().AnyAsync(x => x.Id == id, ct) || await db.Set<PlanningTombstone>().AnyAsync(x => x.Id == id, ct);

    public Task SaveAsync(Project project, CancellationToken ct, IReadOnlyList<PlanningDeletion>? deleted = null) => transaction.ExecuteAsync(async token =>
    {
        var entry = db.Entry(project);
        if (entry.State == EntityState.Detached) db.Add(project);
        db.ChangeTracker.DetectChanges();
        var snapshots = CollectSnapshots(project, deleted ?? []);
        await PersistTombstonesAsync(snapshots.Where(x => x.Deleted), token);
        foreach (var snapshot in snapshots) await journal.AppendAsync(snapshot, token);
    }, ct);

    public async Task DeleteProjectAsync(Guid id, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            var project = await GetProjectAsync(id, token) ?? throw new KeyNotFoundException($"Project {id} was not found.");
            var tombstones = new List<EntitySnapshot> { Tombstone("planning.project", project.Id, project.Version + 1) };
            tombstones.AddRange(project.Milestones.Select(m => Tombstone("planning.milestone", m.Id, m.Version + 1)));
            tombstones.AddRange(project.Milestones.SelectMany(m => m.Features).Select(f => Tombstone("planning.feature", f.Id, f.Version + 1)));
            await PersistTombstonesAsync(tombstones, token);
            db.Remove(project);
            foreach (var snapshot in tombstones) await journal.AppendAsync(snapshot, token);
        }, ct);
    }

    private List<EntitySnapshot> CollectSnapshots(Project project, IReadOnlyList<PlanningDeletion> explicitDeleted)
    {
        var result = new List<EntitySnapshot> { Snapshot("planning.project", project.Id, project.Version, new { title = project.Title, body = project.Description, path = project.Title, url = $"/planning/projects/{project.Id}", archived = project.IsArchived, position = project.Position, updatedAtUtc = project.UpdatedAtUtc }) };
        foreach (var milestone in project.Milestones)
        {
            var milestonePath = $"{project.Title} / {milestone.Title}";
            result.Add(Snapshot("planning.milestone", milestone.Id, milestone.Version, new { title = milestone.Title, body = milestone.Description, path = milestonePath, url = $"/planning/projects/{project.Id}/milestones/{milestone.Id}", archived = project.IsArchived, position = milestone.Position, updatedAtUtc = milestone.UpdatedAtUtc }));
            foreach (var feature in milestone.Features)
                result.Add(Snapshot("planning.feature", feature.Id, feature.Version, new { title = feature.Title, body = feature.Description, path = $"{milestonePath} / {feature.Title}", url = $"/planning/projects/{project.Id}/milestones/{milestone.Id}/features/{feature.Id}", featureStatus = feature.Status.ToString().ToLowerInvariant(), archived = project.IsArchived, position = feature.Position, updatedAtUtc = feature.UpdatedAtUtc }));
        }

        var tombstones = explicitDeleted.ToDictionary(x => (x.Type, x.Id), x => x.Version);
        foreach (var entry in db.ChangeTracker.Entries<OrderedEntity>().Where(x => x.State == EntityState.Deleted))
        {
            var entity = entry.Entity;
            var kind = entity is Milestone ? "planning.milestone" : "planning.feature";
            tombstones.TryAdd((kind, entity.Id), entity.Version + 1);
        }
        result.AddRange(tombstones.Select(x => Tombstone(x.Key.Type, x.Key.Id, x.Value)));
        return result;
    }

    private async Task PersistTombstonesAsync(IEnumerable<EntitySnapshot> snapshots, CancellationToken ct)
    {
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.Deleted) continue;
            var row = await db.Set<PlanningTombstone>().SingleOrDefaultAsync(x => x.Type == snapshot.Type && x.Id == snapshot.Id, ct);
            if (row is null) db.Add(new PlanningTombstone { Type = snapshot.Type, Id = snapshot.Id, Version = snapshot.Version });
            else row.Version = Math.Max(row.Version, snapshot.Version);
        }
    }

    private static EntitySnapshot Snapshot<T>(string type, Guid id, long version, T payload) =>
        new(type, id, version, false, JsonSerializer.SerializeToElement(payload));
    private static EntitySnapshot Tombstone(string type, Guid id, long version) => new(type, id, version, true, null);
}

public sealed class PlanningLinkValidator(PlatformDbContext db) : IPlanningLinkValidator, IPlanningPathReader
{
    public async Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken ct = default)
    {
        if (link.ProjectId is null && link.MilestoneId is null && link.FeatureId is null) return new(true, []);
        if (link.ProjectId is null || link.MilestoneId is null || link.FeatureId is null) return new(false, ["Project, milestone and feature must be provided together."]);
        var project = await db.Set<Project>().AsNoTracking().Include(x => x.Milestones).ThenInclude(x => x.Features).SingleOrDefaultAsync(x => x.Id == link.ProjectId, ct);
        return project?.Milestones.Any(m => m.Id == link.MilestoneId && m.Features.Any(f => f.Id == link.FeatureId)) == true
            ? new(true, []) : new(false, ["The project, milestone and feature do not form an existing path."]);
    }

    public async Task<PlanningPath?> ReadPathAsync(PlanningLink link, CancellationToken ct = default)
    {
        if (link.ProjectId is null) return null;
        var tracked = db.ChangeTracker.Entries<Project>()
            .FirstOrDefault(entry => entry.Entity.Id == link.ProjectId && entry.State != EntityState.Deleted)?.Entity;
        if (tracked is not null)
        {
            var projectEntry = db.Entry(tracked);
            if (!projectEntry.Collection(x => x.Milestones).IsLoaded)
                await projectEntry.Collection(x => x.Milestones).LoadAsync(ct);
            var trackedMilestone = link.MilestoneId is { } trackedMilestoneId
                ? tracked.Milestones.SingleOrDefault(x => x.Id == trackedMilestoneId)
                : null;
            if (trackedMilestone is not null && link.FeatureId is not null)
            {
                var milestoneEntry = db.Entry(trackedMilestone);
                if (!milestoneEntry.Collection(x => x.Features).IsLoaded)
                    await milestoneEntry.Collection(x => x.Features).LoadAsync(ct);
            }
            return BuildPath(tracked, link);
        }

        var project = await db.Set<Project>().AsNoTracking().Include(x => x.Milestones).ThenInclude(x => x.Features).SingleOrDefaultAsync(x => x.Id == link.ProjectId, ct);
        return project is null ? null : BuildPath(project, link);
    }

    private static PlanningPath? BuildPath(Project project, PlanningLink link)
    {
        if (project is null) return null;
        var milestone = link.MilestoneId is { } mid ? project.Milestones.SingleOrDefault(x => x.Id == mid) : null;
        var feature = link.FeatureId is { } fid ? milestone?.Features.SingleOrDefault(x => x.Id == fid) : null;
        if (link.MilestoneId is not null && milestone is null || link.FeatureId is not null && feature is null) return null;
        var path = string.Join(" / ", new[] { project.Title, milestone?.Title, feature?.Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var url = feature is not null ? $"/planning/projects/{project.Id}/milestones/{milestone!.Id}/features/{feature.Id}" : milestone is not null ? $"/planning/projects/{project.Id}/milestones/{milestone.Id}" : $"/planning/projects/{project.Id}";
        return new(project.Title, milestone?.Title, feature?.Title, path, url);
    }
}
