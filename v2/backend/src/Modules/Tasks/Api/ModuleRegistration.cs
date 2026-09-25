using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;

namespace PersonalDashboard.V2.Tasks.Api;

public static class TasksApiModule
{
    public static IEndpointRouteBuilder MapTasksApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v2/tasks").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (TaskVersionConflictException ex) { return Results.Conflict(new { error = ex.Message, actualVersion = ex.ActualVersion }); }
            catch (InvalidPlanningLinkException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        group.MapGet("", (TaskLocation? location, TasksService service, CancellationToken ct) => service.ListAsync(location, ct));
        group.MapGet("/{id:guid}", async (Guid id, TasksService service, CancellationToken ct) => await service.GetAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapPost("", async (TaskCreate input, TasksService service, CancellationToken ct) => Results.Created($"/api/v2/tasks", await service.CreateAsync(input.Title, input.Description, input.ProjectId, input.MilestoneId, input.FeatureId, input.SectionId, ct)));
        group.MapPut("/{id:guid}", (Guid id, TaskEdit input, TasksService service, CancellationToken ct) => service.EditAsync(id, input.ExpectedVersion, input.Title, input.Description, ct));
        group.MapPut("/{id:guid}/section", (Guid id, TaskSectionEdit input, TasksService service, CancellationToken ct) => service.SetSectionAsync(id, input.ExpectedVersion, input.SectionId, ct));
        group.MapPost("/{id:guid}/today", (Guid id, VersionInput input, TasksService service, CancellationToken ct) => service.MoveToTodayAsync(id, input.ExpectedVersion, ct));
        group.MapPost("/{id:guid}/backlog", (Guid id, VersionInput input, TasksService service, CancellationToken ct) => service.MoveToBacklogAsync(id, input.ExpectedVersion, ct));
        group.MapPost("/{id:guid}/planning", (Guid id, VersionInput input, TasksService service, CancellationToken ct) => service.ReturnToPlanAsync(id, input.ExpectedVersion, ct));
        group.MapPut("/{id:guid}/status", (Guid id, StatusInput input, TasksService service, CancellationToken ct) => service.SetStatusAsync(id, input.ExpectedVersion, input.Status, ct));
        group.MapPost("/{id:guid}/archive", (Guid id, VersionInput input, TasksService service, CancellationToken ct) => service.ArchiveAsync(id, input.ExpectedVersion, ct));
        group.MapPost("/{id:guid}/restore", (Guid id, VersionInput input, TasksService service, CancellationToken ct) => service.RestoreAsync(id, input.ExpectedVersion, null, ct));
        group.MapDelete("/{id:guid}", async (Guid id, [FromBody] VersionInput input, TasksService service, CancellationToken ct) => { await service.DeleteAsync(id, input.ExpectedVersion, ct); return Results.NoContent(); });
        group.MapPut("/order", (TaskOrderInput input, TasksService service, CancellationToken ct) => service.ReorderTasksAsync(input.Location, input.SectionId, input.Items, ct, input.ProjectId, input.MilestoneId, input.FeatureId));
        group.MapGet("/sections", (TaskLocation location, TasksService service, CancellationToken ct) => service.SectionsAsync(location, ct));
        group.MapPost("/sections", (TaskSectionCreate input, TasksService service, CancellationToken ct) => service.CreateSectionAsync(input.Name, input.Location, ct));
        group.MapPut("/sections/{id:guid}", (Guid id, TaskSectionEditInput input, TasksService service, CancellationToken ct) => service.RenameSectionAsync(id, input.ExpectedVersion, input.Name, ct));
        group.MapPut("/sections/order", (TaskSectionOrderInput input, TasksService service, CancellationToken ct) => service.ReorderSectionsAsync(input.Location, input.ExpectedVersion, input.Ids, ct));
        group.MapDelete("/sections/{id:guid}", async (Guid id, [FromBody] VersionInput input, TasksService service, CancellationToken ct) => { await service.DeleteSectionAsync(id, input.ExpectedVersion, ct); return Results.NoContent(); });
        return endpoints;
    }

    public sealed record TaskCreate(string Title, string? Description, Guid? ProjectId, Guid? MilestoneId, Guid? FeatureId, Guid? SectionId);
    public sealed record TaskEdit(long ExpectedVersion, string Title, string? Description);
    public sealed record TaskSectionEdit(long ExpectedVersion, Guid SectionId);
    public sealed record VersionInput(long ExpectedVersion);
    public sealed record StatusInput(long ExpectedVersion, TaskWorkStatus Status);
    public sealed record TaskOrderInput(TaskLocation Location, Guid? SectionId, IReadOnlyList<TaskOrderItem> Items, Guid? ProjectId = null, Guid? MilestoneId = null, Guid? FeatureId = null);
    public sealed record TaskSectionCreate(string Name, TaskLocation Location);
    public sealed record TaskSectionEditInput(long ExpectedVersion, string Name);
    public sealed record TaskSectionOrderInput(TaskLocation Location, long ExpectedVersion, IReadOnlyList<Guid> Ids);
}
