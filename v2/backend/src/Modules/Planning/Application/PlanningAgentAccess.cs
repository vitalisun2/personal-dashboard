using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Planning.Domain;

namespace PersonalDashboard.V2.Planning.Application;

public sealed class PlanningAgentAccess(PlanningService planning) : IPlanningAgentAccess
{
    public async Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken ct = default)
    {
        if (kind == PlanningEntityKind.Project) return (await planning.GetAsync(id, ct)) is { } p ? ProjectState(p) : null;
        if (kind == PlanningEntityKind.Milestone)
        {
            var projects = await planning.ListAsync(true, ct);
            foreach (var p in projects) if (p.Milestones.FirstOrDefault(x => x.Id == id) is { } m) return MilestoneState(p.Id, m);
            return null;
        }
        else
        {
            var projects = await planning.ListAsync(true, ct);
            foreach (var p in projects) foreach (var m in p.Milestones) if (m.Features.FirstOrDefault(x => x.Id == id) is { } f) return FeatureState(p.Id, m.Id, f);
            return null;
        }
    }

    public async Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken ct = default)
    {
        try
        {
            PlanningEntityState? created = null;
            switch (mutation.Operation)
            {
                case PlanningMutationKind.Create:
                    if (mutation.Kind == PlanningEntityKind.Project) created = ProjectState(await planning.CreateProjectAsync(mutation.Title ?? "", mutation.Description, ct, mutation.Id));
                    else if (mutation.Kind == PlanningEntityKind.Milestone && mutation.ProjectId is { } projectId)
                    {
                        var result = await planning.AddMilestoneAsync(projectId, RequiredParentVersion(mutation), mutation.Title ?? "", mutation.Description, ct, mutation.Id);
                        created = MilestoneState(projectId, result.Milestones.Single(x => x.Id == mutation.Id));
                    }
                    else if (mutation.Kind == PlanningEntityKind.Feature && mutation.ProjectId is { } pId && mutation.MilestoneId is { } mId)
                    {
                        var result = await planning.AddFeatureAsync(pId, mId, RequiredParentVersion(mutation), mutation.Title ?? "", mutation.Description, ct, mutation.Id);
                        created = FeatureState(pId, mId, result.Milestones.Single(x => x.Id == mId).Features.Single(x => x.Id == mutation.Id));
                    }
                    else throw new ArgumentException("Create requires a valid parent for this planning entity.");
                    return new(true, created, null);
                case PlanningMutationKind.Update:
                    if (mutation.Kind == PlanningEntityKind.Project) await planning.EditProjectAsync(mutation.Id, RequiredVersion(mutation), mutation.Title ?? "", mutation.Description, ct);
                    else if (mutation.Kind == PlanningEntityKind.Milestone && mutation.ProjectId is { } projectId) await planning.EditMilestoneAsync(projectId, mutation.Id, RequiredVersion(mutation), mutation.Title ?? "", mutation.Description, ct);
                    else if (mutation.Kind == PlanningEntityKind.Feature && mutation.ProjectId is { } pId && mutation.MilestoneId is { } mId) await planning.EditFeatureAsync(pId, mId, mutation.Id, RequiredVersion(mutation), mutation.Title ?? "", mutation.Description, ct);
                    else throw new ArgumentException("Update requires the planning parents.");
                    break;
                case PlanningMutationKind.SetFeatureStatus:
                    if (mutation.Kind != PlanningEntityKind.Feature || mutation.ProjectId is not { } featureProject || mutation.MilestoneId is not { } featureMilestone || !Enum.TryParse<FeatureStatus>(mutation.FeatureStatus, true, out var status)) throw new ArgumentException("SetFeatureStatus requires a feature and a valid status.");
                    await planning.SetFeatureStatusAsync(featureProject, featureMilestone, mutation.Id, RequiredVersion(mutation), status, ct); break;
                case PlanningMutationKind.Archive:
                case PlanningMutationKind.Restore:
                    if (mutation.Kind != PlanningEntityKind.Project) throw new ArgumentException("Only projects can be archived.");
                    await planning.ArchiveProjectAsync(mutation.Id, RequiredVersion(mutation), mutation.Operation == PlanningMutationKind.Archive, ct); break;
                case PlanningMutationKind.Delete:
                    if (mutation.Kind == PlanningEntityKind.Project) await planning.DeleteProjectAsync(mutation.Id, RequiredVersion(mutation), ct);
                    else if (mutation.Kind == PlanningEntityKind.Milestone && mutation.ProjectId is { } deleteProject) await planning.DeleteMilestoneAsync(deleteProject, mutation.Id, RequiredVersion(mutation), ct);
                    else if (mutation.Kind == PlanningEntityKind.Feature && mutation.ProjectId is { } dp && mutation.MilestoneId is { } dm) await planning.DeleteFeatureAsync(dp, dm, mutation.Id, RequiredVersion(mutation), ct);
                    else throw new ArgumentException("Delete requires the planning parents.");
                    return new(true, null, null);
                case PlanningMutationKind.Reorder:
                    if (mutation.Order is null) throw new ArgumentException("Reorder requires an ordered set with expected versions.");
                    if (mutation.Kind == PlanningEntityKind.Project)
                    {
                        var projects = await planning.ListAsync(true, ct); ValidateOrder(mutation.Order, projects.Select(x => (x.Id, x.Version)));
                        ValidateSourceEntityVersion(mutation);
                        var reordered = await planning.ReorderProjectsAsync(mutation.Order, ct);
                        return new(true, reordered.SingleOrDefault(x => x.Id == mutation.Id) is { } reorderedProject ? ProjectState(reorderedProject) : null, null);
                    }
                    if (mutation.Kind == PlanningEntityKind.Milestone && mutation.ProjectId is { } reorderProject)
                    {
                        var project = await planning.GetAsync(reorderProject, ct) ?? throw new KeyNotFoundException();
                        ValidateOrder(mutation.Order, project.Milestones.Select(x => (x.Id, x.Version)));
                        ValidateSourceEntityVersion(mutation);
                        await planning.ReorderMilestonesAsync(reorderProject, RequiredParentVersion(mutation), mutation.Order.Select(x => x.Id).ToArray(), ct);
                    }
                    else if (mutation.Kind == PlanningEntityKind.Feature && mutation.ProjectId is { } rp && mutation.MilestoneId is { } rm)
                    {
                        var project = await planning.GetAsync(rp, ct) ?? throw new KeyNotFoundException(); var features = project.Milestones.Single(x => x.Id == rm).Features;
                        ValidateOrder(mutation.Order, features.Select(x => (x.Id, x.Version)));
                        ValidateSourceEntityVersion(mutation);
                        await planning.ReorderFeaturesAsync(rp, rm, RequiredParentVersion(mutation), mutation.Order.Select(x => x.Id).ToArray(), ct);
                    }
                    else throw new ArgumentException("Only milestones and features can be reordered.");
                    break;
                default: throw new ArgumentException("Unsupported planning operation.");
            }
            var current = await ReadAsync(mutation.Kind, mutation.Id, ct);
            return new(true, current, null);
        }
        catch (Exception ex) when (ex is VersionConflictException or LinkedTaskConflictException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            return new(false, await ReadAsync(mutation.Kind, mutation.Id, ct), ex.Message);
        }
    }

    private static long RequiredVersion(PlanningMutation mutation) => mutation.ExpectedVersion ?? throw new ArgumentException("ExpectedVersion is required.");
    private static long RequiredParentVersion(PlanningMutation mutation) => mutation.ExpectedParentVersion ?? throw new ArgumentException("ExpectedParentVersion is required.");
    private static void ValidateSourceEntityVersion(PlanningMutation mutation)
    {
        var expected = RequiredVersion(mutation);
        if (mutation.Order?.SingleOrDefault(x => x.Id == mutation.Id) is not { } source || source.ExpectedVersion != expected)
            throw new ArgumentException("ExpectedVersion must match the source entity version in the order.");
    }
    private static void ValidateOrder(IReadOnlyList<VersionedEntityId> order, IEnumerable<(Guid Id, long Version)> current)
    {
        var values = current.ToDictionary(x => x.Id, x => x.Version);
        if (order.Count != values.Count || order.Select(x => x.Id).Distinct().Count() != order.Count || order.Any(x => !values.TryGetValue(x.Id, out var v) || v != x.ExpectedVersion)) throw new ArgumentException("Order is stale or does not include every current entity.");
    }
    private static PlanningEntityState ProjectState(ProjectView p) => new(PlanningEntityKind.Project, p.Id, null, null, p.Version, p.Title, p.Description, null, p.IsArchived, p.Position);
    private static PlanningEntityState MilestoneState(Guid projectId, MilestoneView m) => new(PlanningEntityKind.Milestone, m.Id, projectId, null, m.Version, m.Title, m.Description, null, false, m.Position);
    private static PlanningEntityState FeatureState(Guid projectId, Guid milestoneId, FeatureView f) => new(PlanningEntityKind.Feature, f.Id, projectId, milestoneId, f.Version, f.Title, f.Description, f.Status.ToString().ToLowerInvariant(), false, f.Position);
}
