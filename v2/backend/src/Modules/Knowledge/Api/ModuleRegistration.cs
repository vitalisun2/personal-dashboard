using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Api;

public static class KnowledgeApiModule
{
    public static IEndpointRouteBuilder MapKnowledgeApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v2/knowledge").WithTags("Knowledge");
        group.MapGet("/tree", async (KnowledgeService service, CancellationToken ct) =>
            Results.Ok(await service.GetTreeAsync(ct)));
        group.MapGet("/nodes/{id:guid}", async (Guid id, KnowledgeService service, CancellationToken ct) =>
        {
            var node = (await service.GetTreeAsync(ct)).FirstOrDefault(n => n.Id == id);
            return node is null ? Results.NotFound() : Results.Ok(node);
        });
        group.MapGet("/search", async (string q, KnowledgeService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(q, ct)));
        group.MapPost("/nodes", async (CreateKnowledgeNode request, KnowledgeService service, CancellationToken ct) =>
        {
            try
            {
                var type = request.Kind switch
                {
                    "section" => KnowledgeNodeType.Section,
                    "document" => KnowledgeNodeType.Document,
                    _ => throw new ArgumentException("Kind must be section or document.")
                };
                var result = await service.CreateWithIdAsync(request.Id ?? Guid.NewGuid(), type,
                    request.Title, request.Markdown ?? string.Empty, request.ParentId, ct);
                return Results.Created($"/api/v2/knowledge/nodes/{result.ChangedNodes[0].Id}", result.ChangedNodes[0]);
            }
            catch (KeyNotFoundException ex) { return Problem(404, ex.Message); }
            catch (ArgumentException ex) { return Problem(400, ex.Message); }
            catch (InvalidOperationException ex) { return Problem(400, ex.Message); }
        });
        group.MapPut("/nodes/{id:guid}", async (Guid id, EditKnowledgeNode request, KnowledgeService service, CancellationToken ct) =>
        {
            try
            {
                if (request.Kind == "section")
                {
                    var result = await service.RenameAsync(id, request.Title, request.ExpectedVersion, ct);
                    return Results.Ok(result.ChangedNodes.First(n => n.Id == id));
                }
                if (request.Kind != "document") return Problem(400, "Kind must be section or document.");
                var edit = await service.EditDocumentAsync(id, request.Title, request.Markdown, request.ExpectedVersion, ct);
                return Results.Ok(edit.ChangedNodes.First(n => n.Id == id));
            }
            catch (KnowledgeVersionConflictException ex) { return Problem(409, ex.Message); }
            catch (KnowledgeConcurrentWriteException ex) { return Problem(409, ex.Message); }
            catch (KeyNotFoundException ex) { return Problem(404, ex.Message); }
            catch (ArgumentException ex) { return Problem(400, ex.Message); }
            catch (InvalidOperationException ex) { return Problem(400, ex.Message); }
        });
        group.MapPost("/nodes/{id:guid}/move", async (Guid id, MoveKnowledgeNode request, KnowledgeService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.MoveAsync(id, request.TargetId, request.Placement, request.ExpectedVersion, ct);
                return Results.Ok(result);
            }
            catch (KnowledgeVersionConflictException ex) { return Problem(409, ex.Message); }
            catch (KnowledgeConcurrentWriteException ex) { return Problem(409, ex.Message); }
            catch (KeyNotFoundException ex) { return Problem(404, ex.Message); }
            catch (ArgumentException ex) { return Problem(400, ex.Message); }
            catch (InvalidOperationException ex) { return Problem(400, ex.Message); }
        });
        group.MapDelete("/nodes/{id:guid}", async (Guid id, long expectedVersion, KnowledgeService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.DeleteAsync(id, expectedVersion, ct)); }
            catch (KnowledgeVersionConflictException ex) { return Problem(409, ex.Message); }
            catch (KnowledgeConcurrentWriteException ex) { return Problem(409, ex.Message); }
            catch (KeyNotFoundException ex) { return Problem(404, ex.Message); }
            catch (InvalidOperationException ex) { return Problem(400, ex.Message); }
        });
        return endpoints;
    }

    private static IResult Problem(int status, string detail) =>
        Results.Problem(statusCode: status, title: status == 409 ? "Version conflict" : status == 404 ? "Not found" : "Invalid Knowledge request", detail: detail);
}

public sealed record CreateKnowledgeNode(string Kind, string Title, Guid? ParentId, Guid? Id = null, string? Markdown = null);
public sealed record EditKnowledgeNode(string Kind, long ExpectedVersion, string Title, string? Markdown);
public sealed record MoveKnowledgeNode(Guid? TargetId, string Placement, long ExpectedVersion);
