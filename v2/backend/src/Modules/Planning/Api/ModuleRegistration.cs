using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Planning.Application;
using PersonalDashboard.V2.Planning.Domain;
using PersonalDashboard.V2.Contracts.AgentAccess;

namespace PersonalDashboard.V2.Planning.Api;

public static class PlanningApiModule
{
    public static IEndpointRouteBuilder MapPlanningApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v2/planning").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (VersionConflictException ex) { return Results.Conflict(new { error = ex.Message, actualVersion = ex.ActualVersion }); }
            catch (LinkedTaskConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        group.MapGet("/projects", (bool? includeArchived, PlanningService service, CancellationToken ct) => service.ListAsync(includeArchived ?? false, ct));
        group.MapPut("/projects/order", (ProjectOrderInput input, PlanningService service, CancellationToken ct) => service.ReorderProjectsAsync(input.Items, ct));
        group.MapGet("/projects/{id:guid}", async (Guid id, PlanningService service, CancellationToken ct) => await service.GetAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapPost("/projects", async (ProjectInput input, PlanningService service, CancellationToken ct) => Results.Created("", await service.CreateProjectAsync(input.Title, input.Description, ct)));
        group.MapPut("/projects/{id:guid}", (Guid id, ProjectEdit input, PlanningService service, CancellationToken ct) => service.EditProjectAsync(id, input.ExpectedVersion, input.Title, input.Description, ct));
        group.MapPost("/projects/{id:guid}/archive", (Guid id, VersionInput input, PlanningService service, CancellationToken ct) => service.ArchiveProjectAsync(id, input.ExpectedVersion, true, ct));
        group.MapPost("/projects/{id:guid}/restore", (Guid id, VersionInput input, PlanningService service, CancellationToken ct) => service.ArchiveProjectAsync(id, input.ExpectedVersion, false, ct));
        group.MapDelete("/projects/{id:guid}", async (Guid id, [FromBody] VersionInput input, PlanningService service, CancellationToken ct) => { await service.DeleteProjectAsync(id, input.ExpectedVersion, ct); return Results.NoContent(); });
        group.MapPost("/projects/{id:guid}/milestones", (Guid id, ChildInput input, PlanningService service, CancellationToken ct) => service.AddMilestoneAsync(id, input.ExpectedParentVersion, input.Title, input.Description, ct));
        group.MapPut("/projects/{id:guid}/milestones/{milestoneId:guid}", (Guid id, Guid milestoneId, ChildEdit input, PlanningService service, CancellationToken ct) => service.EditMilestoneAsync(id, milestoneId, input.ExpectedVersion, input.Title, input.Description, ct));
        group.MapPut("/projects/{id:guid}/milestones/order", (Guid id, OrderInput input, PlanningService service, CancellationToken ct) => service.ReorderMilestonesAsync(id, input.ExpectedVersion, input.Ids, ct));
        group.MapDelete("/projects/{id:guid}/milestones/{milestoneId:guid}", (Guid id, Guid milestoneId, [FromBody] VersionInput input, PlanningService service, CancellationToken ct) => service.DeleteMilestoneAsync(id, milestoneId, input.ExpectedVersion, ct));
        group.MapPost("/projects/{id:guid}/milestones/{milestoneId:guid}/features", (Guid id, Guid milestoneId, ChildInput input, PlanningService service, CancellationToken ct) => service.AddFeatureAsync(id, milestoneId, input.ExpectedParentVersion, input.Title, input.Description, ct));
        group.MapPut("/projects/{id:guid}/milestones/{milestoneId:guid}/features/order", (Guid id, Guid milestoneId, OrderInput input, PlanningService service, CancellationToken ct) => service.ReorderFeaturesAsync(id, milestoneId, input.ExpectedVersion, input.Ids, ct));
        group.MapPut("/projects/{id:guid}/milestones/{milestoneId:guid}/features/{featureId:guid}", (Guid id, Guid milestoneId, Guid featureId, FeatureEdit input, PlanningService service, CancellationToken ct) => service.EditFeatureAsync(id, milestoneId, featureId, input.ExpectedVersion, input.Title, input.Description, ct));
        group.MapPut("/projects/{id:guid}/milestones/{milestoneId:guid}/features/{featureId:guid}/status", (Guid id, Guid milestoneId, Guid featureId, FeatureStatusInput input, PlanningService service, CancellationToken ct) => service.SetFeatureStatusAsync(id, milestoneId, featureId, input.ExpectedVersion, input.Status, ct));
        group.MapDelete("/projects/{id:guid}/milestones/{milestoneId:guid}/features/{featureId:guid}", (Guid id, Guid milestoneId, Guid featureId, [FromBody] VersionInput input, PlanningService service, CancellationToken ct) => service.DeleteFeatureAsync(id, milestoneId, featureId, input.ExpectedVersion, ct));
        return endpoints;
    }

    public sealed record ProjectInput(string Title, string? Description);
    public sealed record VersionInput(long ExpectedVersion);
    public sealed record ProjectEdit(long ExpectedVersion, string Title, string? Description);
    public sealed record ChildInput(long ExpectedParentVersion, string Title, string? Description);
    public sealed record ChildEdit(long ExpectedVersion, string Title, string? Description);
    public sealed record FeatureEdit(long ExpectedVersion, string Title, string? Description);
    public sealed record FeatureStatusInput(long ExpectedVersion, FeatureStatus Status);
    public sealed record OrderInput(long ExpectedVersion, IReadOnlyList<Guid> Ids);
    public sealed record ProjectOrderInput(IReadOnlyList<VersionedEntityId> Items);
}
