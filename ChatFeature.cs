using AgentChat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>Temporary compatibility routes for pre-scoped task-chat links.</summary>
static class LegacyTaskChatRoutes
{
    public static IEndpointRouteBuilder MapLegacyTaskChatApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/sessions", async (ChatMessageRequest request, ChatService service, CancellationToken ct) =>
            ToLegacyResult(await service.SendAsync(null, ChatScope.Tasks, request, ct)));
        app.MapGet("/api/chat/sessions/{id:guid}", async (Guid id, ChatService service, CancellationToken ct) =>
            (await service.GetAsync(id, ChatScope.Tasks, ct)) is { } session ? Results.Ok(session) : Results.NotFound());
        app.MapPost("/api/chat/sessions/{id:guid}/messages", async (Guid id, ChatMessageRequest request, ChatService service, CancellationToken ct) =>
            ToLegacyResult(await service.SendAsync(id, ChatScope.Tasks, request, ct)));
        return app;
    }

    private static IResult ToLegacyResult((ChatSession? Session, ChatReply? Reply, string? Error) result) =>
        result.Error is not null ? Results.BadRequest(new { message = result.Error }) : Results.Ok(new { session = result.Session, response = result.Reply });
}
