using KnowledgeBase.Api.Application;
using KnowledgeBase.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Api;

public static class KnowledgeApiExtensions
{
    public static IServiceCollection AddKnowledgeBase(this IServiceCollection services)
    {
        services.AddSingleton<IKnowledgeStore, JsonKnowledgeStore>();
        services.AddSingleton<KnowledgeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapKnowledgeBaseApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sync/v2/knowledge", async (KnowledgePeerImport input, KnowledgeService service, CancellationToken ct) =>
        {
            if (input.Id == Guid.Empty || string.IsNullOrWhiteSpace(input.Kind) || string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest();
            var imported = await service.ImportSyncNodeAsync(new KnowledgeNodeDto(input.Id, input.Kind, input.Title, input.ParentId, input.Position, input.CreatedAt ?? DateTimeOffset.UtcNow, input.UpdatedAt ?? DateTimeOffset.UtcNow), input.Markdown, false, ct);
            return imported ? Results.NoContent() : Results.Conflict(new { message = "Knowledge parent or payload is incompatible." });
        });
        app.MapDelete("/api/sync/v2/knowledge/{id:guid}", async (Guid id, KnowledgeService service, CancellationToken ct) =>
        {
            await service.ImportSyncNodeAsync(new KnowledgeNodeDto(id, "section", "", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), null, true, ct);
            return Results.NoContent();
        });
        app.MapGet("/api/knowledge/tree", async (KnowledgeService service, CancellationToken ct) => Results.Ok(await service.GetTreeAsync(ct)));
        app.MapGet("/api/knowledge/documents/{id:guid}", async (Guid id, KnowledgeService service, CancellationToken ct) => (await service.GetDocumentAsync(id, ct)) is { } doc ? Results.Ok(doc) : Results.NotFound(new { message = "Документ не найден." }));
        app.MapPost("/api/knowledge/sections", async (CreateSectionRequest request, KnowledgeService service, CancellationToken ct) => { var result = await service.CreateAsync("section", request.Title, null, request.ParentId, ct); return result.Node is null ? Results.BadRequest(new { message = result.Error }) : Results.Created($"/api/knowledge/nodes/{result.Node.Id}", result.Node); });
        app.MapPost("/api/knowledge/documents", async (CreateDocumentRequest request, KnowledgeService service, CancellationToken ct) => { var result = await service.CreateAsync("document", request.Title, request.Content, request.ParentId, ct); return result.Node is null ? Results.BadRequest(new { message = result.Error }) : Results.Created($"/api/knowledge/documents/{result.Node.Id}", result.Node); });
        app.MapPatch("/api/knowledge/nodes/{id:guid}/name", async (Guid id, RenameRequest request, KnowledgeService service, CancellationToken ct) => { var error = await service.RenameAsync(id, request.Title, ct); return error is null ? Results.NoContent() : error == "Узел не найден." ? Results.NotFound(new { message = error }) : Results.BadRequest(new { message = error }); });
        app.MapPut("/api/knowledge/documents/{id:guid}/content", async (Guid id, UpdateContentRequest request, KnowledgeService service, CancellationToken ct) => { var error = await service.UpdateContentAsync(id, request.Content, ct); return error is null ? Results.NoContent() : error == "Документ не найден." ? Results.NotFound(new { message = error }) : Results.BadRequest(new { message = error }); });
        app.MapPost("/api/knowledge/documents/{id:guid}/toggle-version", async (Guid id, KnowledgeService service, CancellationToken ct) => { var result = await service.ToggleDocumentVersionAsync(id, ct); return !result.Found ? Results.NotFound(new { message = "Документ не найден." }) : result.Document is null ? Results.Conflict(new { message = result.Error ?? "Для документа нет предыдущей версии." }) : Results.Ok(result.Document); });
        app.MapPut("/api/knowledge/nodes/{id:guid}/position", async (Guid id, MoveRequest request, KnowledgeService service, CancellationToken ct) => { var error = await service.MoveAsync(id, request.ParentId, request.Order, ct); return error is null ? Results.NoContent() : error == "Узел не найден." ? Results.NotFound(new { message = error }) : Results.BadRequest(new { message = error }); });
        app.MapDelete("/api/knowledge/nodes/{id:guid}", async (Guid id, KnowledgeService service, CancellationToken ct) => { var error = await service.DeleteAsync(id, ct); return error is null ? Results.NoContent() : Results.NotFound(new { message = error }); });
        return app;
    }
}

public sealed record KnowledgePeerImport(Guid Id, string Kind, string Title, Guid? ParentId, int Position, string? Markdown, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);
