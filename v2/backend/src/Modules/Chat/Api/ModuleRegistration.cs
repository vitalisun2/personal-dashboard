using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Chat.Api;

public static class ChatApiModule
{
    public static IEndpointRouteBuilder MapChatApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v2/chat/conversations");
        api.MapPost("", async (IChatConversationStore store, CancellationToken cancellationToken) =>
        {
            var conversation = await store.CreateConversationAsync(Guid.NewGuid(), null, cancellationToken);
            return Results.Ok(new { id = conversation.Id, title = conversation.Title ?? "Новый чат", createdAt = conversation.CreatedAtUtc, updatedAt = conversation.CreatedAtUtc, messages = Array.Empty<object>(), turns = Array.Empty<object>() });
        });
        api.MapGet("", async (IChatConversationStore store, string? cursor, int? pageSize, CancellationToken cancellationToken) =>
        {
            var page = await store.ListConversationsAsync(cursor, pageSize ?? 40, cancellationToken);
            return Results.Ok(new
            {
                conversations = page.Conversations.Select(x => new { id = x.Id, title = x.Title ?? "Новый чат", updatedAt = x.UpdatedAtUtc, turnCount = x.TurnCount }),
                nextCursor = page.NextCursor
            });
        });
        api.MapGet("/{conversationId:guid}", GetConversationAsync);
        api.MapGet("/{conversationId:guid}/turns", GetTurnsAsync);
        api.MapDelete("/{conversationId:guid}/proposals/{proposalId:guid}", DismissProposalAsync);
        return endpoints;
    }

    private static async Task<IResult> GetConversationAsync(Guid conversationId, Guid? turnId,
        IChatConversationStore store, CancellationToken cancellationToken)
    {
        var conversation = await store.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null) return Results.NotFound();
        var turns = new List<ChatTurn>();
        string? cursor = null;
        var pages = turnId.HasValue ? int.MaxValue : 1;
        ChatTurnPage? latest = null;
        for (var i = 0; i < pages; i++)
        {
            latest = await store.GetTurnsPageAsync(conversationId, cursor, 100, cancellationToken);
            turns.InsertRange(0, latest.Turns);
            if (turnId is null || latest.Turns.Any(x => x.Id == turnId) || latest.NextCursor is null) break;
            cursor = latest.NextCursor;
        }
        var turnViews = new List<object>();
        foreach (var turn in turns)
        {
            var proposal = await store.GetProposalForTurnAsync(turn.Id, cancellationToken);
            turnViews.Add(ToTurnView(turn, proposal));
        }
        var updated = turns.Count > 0 ? turns[^1].CreatedAtUtc : conversation.CreatedAtUtc;
        return Results.Ok(new
        {
            id = conversation.Id,
            title = conversation.Title ?? "Новый чат",
            createdAt = conversation.CreatedAtUtc,
            updatedAt = updated,
            messages = Array.Empty<object>(),
            turns = turnViews,
            nextCursor = turnId is null ? latest?.NextCursor : null
        });
    }

    private static async Task<IResult> GetTurnsAsync(Guid conversationId, string? cursor, int? pageSize,
        IChatConversationStore store, CancellationToken cancellationToken)
    {
        if (await store.GetConversationAsync(conversationId, cancellationToken) is null) return Results.NotFound();
        var page = await store.GetTurnsPageAsync(conversationId, cursor, pageSize ?? 100, cancellationToken);
        var turns = new List<object>();
        foreach (var turn in page.Turns)
            turns.Add(ToTurnView(turn, await store.GetProposalForTurnAsync(turn.Id, cancellationToken)));
        return Results.Ok(new { turns, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> DismissProposalAsync(Guid conversationId, Guid proposalId,
        IChatConversationStore store, CancellationToken cancellationToken)
    {
        var proposal = await store.GetProposalAsync(proposalId, cancellationToken);
        if (proposal is null || proposal.ConversationId != conversationId) return Results.NotFound();
        if (proposal.State != ChatProposalState.Pending) return Results.NoContent();
        var changed = await store.TryChangeProposalStateAsync(proposalId, ChatProposalState.Pending, ChatProposalState.Dismissed, null, cancellationToken);
        return changed ? Results.NoContent() : Results.Conflict(new { error = "The proposal is no longer pending." });
    }

    private static object ToTurnView(ChatTurn turn, ChatProposal? proposal) => new
    {
        id = turn.Id,
        userMessage = turn.UserText,
        assistantMessage = turn.AssistantText,
        scope = new { mode = turn.Scope.Mode, entityType = turn.Scope.EntityType, entityId = turn.Scope.EntityId, entityVersion = turn.Scope.EntityVersion },
        requestedModel = turn.RequestedModel,
        actualModel = turn.ActualModel,
        modelRoute = turn.ModelRoute.ToString(),
        fallbackReason = turn.FallbackReason,
        createdAt = turn.CreatedAtUtc,
        sourceReferences = turn.Sources.Select(source => source.Url ?? source.Path ?? $"{source.Kind}:{source.Id}"),
        sourceDetails = turn.Sources.Select(source => new { title = source.Title, url = source.Url, snippet = source.Snippet }),
        proposalId = proposal?.Id,
        proposalStatus = proposal?.State.ToString(),
        changes = proposal?.Actions.Select(action =>
        {
            var split = action.Label.Split('\n', 2);
            return new
            {
                id = action.Id,
                operation = action.Operation,
                entityType = action.EntityType,
                entityId = action.EntityId,
                displayName = split[0],
                preview = split.Length > 1 ? split[1] : split[0],
                before = action.Before,
                after = action.After
            };
        })
    };
}
