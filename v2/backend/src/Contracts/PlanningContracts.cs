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
