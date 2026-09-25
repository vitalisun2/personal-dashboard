using PersonalDashboard.V2.Planning.Domain;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Contracts.AgentAccess;

namespace PersonalDashboard.V2.Planning.Application;

public sealed record ProjectView(Guid Id, string Title, string Description, long Version, bool IsArchived, int Position, int ProgressPercent, IReadOnlyList<MilestoneView> Milestones);
public sealed record MilestoneView(Guid Id, string Title, string Description, int Position, long Version, int ProgressPercent, IReadOnlyList<FeatureView> Features);
public sealed record FeatureView(Guid Id, string Title, string Description, int Position, long Version, FeatureStatus Status);
public sealed record PlanningDeletion(string Type, Guid Id, long Version);

public interface IPlanningRepository
{
    Task<IReadOnlyList<Project>> ListProjectsAsync(bool includeArchived, CancellationToken cancellationToken);
    Task<Project?> GetProjectAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> EntityIdExistsAsync(Guid id, CancellationToken cancellationToken);
    Task SaveAsync(Project project, CancellationToken cancellationToken, IReadOnlyList<PlanningDeletion>? deleted = null);
    Task DeleteProjectAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class PlanningService(IPlanningRepository repository, ITaskPlanningLinkUsage linkedTasks, ITaskPlanningProjectionRefresh taskProjectionRefresh, ITransactionRunner transaction)
{
    public async Task<IReadOnlyList<ProjectView>> ListAsync(bool includeArchived, CancellationToken ct) =>
        (await repository.ListProjectsAsync(includeArchived, ct)).Select(Map).ToArray();

    public async Task<ProjectView?> GetAsync(Guid id, CancellationToken ct) =>
        await repository.GetProjectAsync(id, ct) is { } project ? Map(project) : null;

    public async Task<ProjectView> CreateProjectAsync(string title, string? description, CancellationToken ct, Guid? id = null)
    {
        if (id is { } requested && await repository.EntityIdExistsAsync(requested, ct)) throw new InvalidOperationException("This planning ID already exists.");
        var existing = await repository.ListProjectsAsync(true, ct); var project = new Project(title, description, id, existing.Count == 0 ? 0 : existing.Max(x => x.Position) + 1); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task<IReadOnlyList<ProjectView>> ReorderProjectsAsync(IReadOnlyList<VersionedEntityId> order, CancellationToken ct)
        => await transaction.ExecuteAsync(async token =>
        {
            var existing = (await repository.ListProjectsAsync(true, token)).ToDictionary(x => x.Id);
            if (order.Count != existing.Count || order.Select(x => x.Id).Distinct().Count() != order.Count || order.Any(x => !existing.ContainsKey(x.Id))) throw new ArgumentException("Project order is stale or incomplete.");
            for (var index = 0; index < order.Count; index++)
            {
                var entry = order[index];
                var project = await Required(entry.Id, token);
                Check(project.Version, entry.ExpectedVersion);
                project.SetPosition(index);
                await repository.SaveAsync(project, token);
            }
            return (await repository.ListProjectsAsync(true, token)).Select(Map).ToArray();
        }, ct);

    public async Task<ProjectView> EditProjectAsync(Guid id, long expectedVersion, string title, string? description, CancellationToken ct)
        => await transaction.ExecuteAsync(async token => { var project = await Required(id, token); Check(project.Version, expectedVersion); project.Rename(title, description); await repository.SaveAsync(project, token); await taskProjectionRefresh.RefreshAsync(new PlanningLink(id, null, null), token); return Map(project); }, ct);

    public async Task<ProjectView> ArchiveProjectAsync(Guid id, long expectedVersion, bool archived, CancellationToken ct)
    {
        var project = await Required(id, ct); Check(project.Version, expectedVersion); if (archived) project.Archive(); else project.Restore(); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task DeleteProjectAsync(Guid id, long expectedVersion, CancellationToken ct)
        => await transaction.ExecuteAsync(async token => { var project = await Required(id, token); Check(project.Version, expectedVersion); if (await linkedTasks.IsInUseAsync(new PlanningLink(id, null, null), token)) throw new LinkedTaskConflictException(); await repository.DeleteProjectAsync(id, token); }, ct);

    public async Task<ProjectView> AddMilestoneAsync(Guid projectId, long expectedVersion, string title, string? description, CancellationToken ct, Guid? id = null)
    {
        var project = await Required(projectId, ct); Check(project.Version, expectedVersion); if (id is { } requested && await repository.EntityIdExistsAsync(requested, ct)) throw new InvalidOperationException("This planning ID already exists."); project.AddMilestone(title, description, id); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task<ProjectView> EditMilestoneAsync(Guid projectId, Guid milestoneId, long expectedVersion, string title, string? description, CancellationToken ct)
        => await transaction.ExecuteAsync(async token => { var project = await Required(projectId, token); var item = project.Milestones.Single(x => x.Id == milestoneId); Check(item.Version, expectedVersion); item.Rename(title, description); project.RecordChildChange(); await repository.SaveAsync(project, token); await taskProjectionRefresh.RefreshAsync(new PlanningLink(projectId, milestoneId, null), token); return Map(project); }, ct);

    public async Task<ProjectView> ReorderMilestonesAsync(Guid projectId, long expectedVersion, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var project = await Required(projectId, ct); Check(project.Version, expectedVersion); project.ReorderMilestones(ids); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task<ProjectView> DeleteMilestoneAsync(Guid projectId, Guid milestoneId, long expectedVersion, CancellationToken ct)
        => await transaction.ExecuteAsync(async token => { var project = await Required(projectId, token); var item = project.Milestones.Single(x => x.Id == milestoneId); Check(item.Version, expectedVersion); if (await linkedTasks.IsInUseAsync(new PlanningLink(projectId, milestoneId, null), token)) throw new LinkedTaskConflictException(); var deleted = new List<PlanningDeletion> { new("planning.milestone", item.Id, item.Version + 1) }; deleted.AddRange(item.Features.Select(feature => new PlanningDeletion("planning.feature", feature.Id, feature.Version + 1))); project.RemoveMilestone(milestoneId); await repository.SaveAsync(project, token, deleted); return Map(project); }, ct);

    public async Task<ProjectView> AddFeatureAsync(Guid projectId, Guid milestoneId, long expectedVersion, string title, string? description, CancellationToken ct, Guid? id = null)
    {
        var project = await Required(projectId, ct); var item = project.Milestones.Single(x => x.Id == milestoneId); Check(item.Version, expectedVersion); if (id is { } requested && await repository.EntityIdExistsAsync(requested, ct)) throw new InvalidOperationException("This planning ID already exists."); item.AddFeature(title, description, id); project.RecordChildChange(); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task<ProjectView> EditFeatureAsync(Guid projectId, Guid milestoneId, Guid featureId, long expectedVersion, string title, string? description, CancellationToken ct)
        => await transaction.ExecuteAsync(async token => { var project = await Required(projectId, token); var milestone = project.Milestones.Single(x => x.Id == milestoneId); var feature = milestone.Features.Single(x => x.Id == featureId); Check(feature.Version, expectedVersion); feature.Rename(title, description); milestone.RecordChildChange(); project.RecordChildChange(); await repository.SaveAsync(project, token); await taskProjectionRefresh.RefreshAsync(new PlanningLink(projectId, milestoneId, featureId), token); return Map(project); }, ct);

    public async Task<ProjectView> SetFeatureStatusAsync(Guid projectId, Guid milestoneId, Guid featureId, long expectedVersion, FeatureStatus status, CancellationToken ct)
    {
        var project = await Required(projectId, ct); var milestone = project.Milestones.Single(x => x.Id == milestoneId); var feature = milestone.Features.Single(x => x.Id == featureId); Check(feature.Version, expectedVersion); feature.SetStatus(status); milestone.RecordChildChange(); project.RecordChildChange(); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task<ProjectView> ReorderFeaturesAsync(Guid projectId, Guid milestoneId, long expectedVersion, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var project = await Required(projectId, ct); var item = project.Milestones.Single(x => x.Id == milestoneId); Check(item.Version, expectedVersion); if (item.ReorderFeatures(ids)) project.RecordChildChange(); await repository.SaveAsync(project, ct); return Map(project);
    }

    public async Task<ProjectView> DeleteFeatureAsync(Guid projectId, Guid milestoneId, Guid featureId, long expectedVersion, CancellationToken ct)
        => await transaction.ExecuteAsync(async token => { var project = await Required(projectId, token); var milestone = project.Milestones.Single(x => x.Id == milestoneId); var feature = milestone.Features.Single(x => x.Id == featureId); Check(feature.Version, expectedVersion); if (await linkedTasks.IsInUseAsync(new PlanningLink(projectId, milestoneId, featureId), token)) throw new LinkedTaskConflictException(); var deleted = new[] { new PlanningDeletion("planning.feature", feature.Id, feature.Version + 1) }; milestone.RemoveFeature(featureId); project.RecordChildChange(); await repository.SaveAsync(project, token, deleted); return Map(project); }, ct);

    private async Task<Project> Required(Guid id, CancellationToken ct) => await repository.GetProjectAsync(id, ct) ?? throw new KeyNotFoundException($"Project {id} was not found.");
    private static void Check(long actual, long expected) { if (actual != expected) throw new VersionConflictException(actual, expected); }
    private static ProjectView Map(Project x) => new(x.Id, x.Title, x.Description, x.Version, x.IsArchived, x.Position, x.ProgressPercent, x.Milestones.OrderBy(m => m.Position).Select(m => new MilestoneView(m.Id, m.Title, m.Description, m.Position, m.Version, m.ProgressPercent, m.Features.OrderBy(f => f.Position).Select(f => new FeatureView(f.Id, f.Title, f.Description, f.Position, f.Version, f.Status)).ToArray())).ToArray());
}

public sealed class VersionConflictException(long actual, long expected) : Exception($"Expected version {expected}, current version is {actual}.") { public long ActualVersion { get; } = actual; public long ExpectedVersion { get; } = expected; }
public sealed class LinkedTaskConflictException() : Exception("Planning item has linked tasks. Move or delete those tasks first.");
