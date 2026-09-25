namespace PersonalDashboard.V2.Contracts.Planning;

public sealed record PlanningLink(Guid? ProjectId, Guid? MilestoneId, Guid? FeatureId);

public sealed record PlanningLinkValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public interface IPlanningLinkValidator
{
    Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken cancellationToken = default);
}

public interface ITaskPlanningLinkUsage
{
    Task<bool> IsInUseAsync(PlanningLink link, CancellationToken cancellationToken = default);
}

// Called after a Planning ancestor changes so Tasks can refresh the derived
// search paths of its own linked task records without Planning editing them.
public interface ITaskPlanningProjectionRefresh
{
    Task RefreshAsync(PlanningLink changedAncestor, CancellationToken cancellationToken = default);
}

public sealed record PlanningPath(
    string ProjectTitle,
    string? MilestoneTitle,
    string? FeatureTitle,
    string Path,
    string? Url);

public interface IPlanningPathReader
{
    Task<PlanningPath?> ReadPathAsync(PlanningLink link, CancellationToken cancellationToken = default);
}
