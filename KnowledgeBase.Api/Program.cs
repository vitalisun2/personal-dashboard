using KnowledgeBase.Api.Application;
using KnowledgeBase.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var origins = (Environment.GetEnvironmentVariable("KNOWLEDGE_ALLOWED_ORIGINS") ?? "http://localhost:8080,http://127.0.0.1:8080").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<IKnowledgeStore, JsonKnowledgeStore>();
builder.Services.AddSingleton<KnowledgeService>();
var app = builder.Build(); app.UseCors();

app.MapGet("/api/knowledge/tree", async (KnowledgeService service, CancellationToken ct) => Results.Ok(await service.GetTreeAsync(ct)));
app.MapGet("/api/knowledge/documents/{id:guid}", async (Guid id, KnowledgeService service, CancellationToken ct) => (await service.GetDocumentAsync(id, ct)) is { } doc ? Results.Ok(doc) : Results.NotFound(new { message = "Документ не найден." }));
app.MapPost("/api/knowledge/sections", async (CreateSectionRequest request, KnowledgeService service, CancellationToken ct) => { var result = await service.CreateAsync("section", request.Title, null, request.ParentId, ct); return result.Node is null ? Results.BadRequest(new { message = result.Error }) : Results.Created($"/api/knowledge/nodes/{result.Node.Id}", result.Node); });
app.MapPost("/api/knowledge/documents", async (CreateDocumentRequest request, KnowledgeService service, CancellationToken ct) => { var result = await service.CreateAsync("document", request.Title, request.Content, request.ParentId, ct); return result.Node is null ? Results.BadRequest(new { message = result.Error }) : Results.Created($"/api/knowledge/documents/{result.Node.Id}", result.Node); });
app.MapPatch("/api/knowledge/nodes/{id:guid}/name", async (Guid id, RenameRequest request, KnowledgeService service, CancellationToken ct) => { var error = await service.RenameAsync(id, request.Title, ct); return error is null ? Results.NoContent() : error == "Узел не найден." ? Results.NotFound(new { message = error }) : Results.BadRequest(new { message = error }); });
app.MapPut("/api/knowledge/documents/{id:guid}/content", async (Guid id, UpdateContentRequest request, KnowledgeService service, CancellationToken ct) => { var error = await service.UpdateContentAsync(id, request.Content, ct); return error is null ? Results.NoContent() : error == "Документ не найден." ? Results.NotFound(new { message = error }) : Results.BadRequest(new { message = error }); });
app.MapPut("/api/knowledge/nodes/{id:guid}/position", async (Guid id, MoveRequest request, KnowledgeService service, CancellationToken ct) => { var error = await service.MoveAsync(id, request.ParentId, request.Order, ct); return error is null ? Results.NoContent() : error == "Узел не найден." ? Results.NotFound(new { message = error }) : Results.BadRequest(new { message = error }); });
app.MapDelete("/api/knowledge/nodes/{id:guid}", async (Guid id, KnowledgeService service, CancellationToken ct) => { var error = await service.DeleteAsync(id, ct); return error is null ? Results.NoContent() : Results.NotFound(new { message = error }); });
app.Run();

public partial class Program { }
