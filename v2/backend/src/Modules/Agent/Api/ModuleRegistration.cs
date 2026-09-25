using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Contracts.Transactions;
using System.Text.Json;

namespace PersonalDashboard.V2.Agent.Api;

public static class AgentApiModule
{
    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v2/agent/conversations");
        api.MapPost("/{conversationId:guid}/turns", SendTurnAsync);
        api.MapPost("/{conversationId:guid}/proposals/{proposalId:guid}/confirm", ConfirmProposalAsync);
        return endpoints;
    }

    private static async Task<IResult> SendTurnAsync(Guid conversationId, SendTurnRequest request,
        IChatConversationStore chats, IAgentTurnService agent, ITransactionRunner transactions, ISearchIndexer search,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required." });
        var requestedModel = request.RequestedModel is "Gemma" or "DeepSeek" ? request.RequestedModel : null;
        if (requestedModel is null) return Results.BadRequest(new { error = "Model must be Gemma or DeepSeek." });
        var scope = new AgentScope(request.Scope?.Mode ?? "general", request.Scope?.EntityType, request.Scope?.EntityId,
            request.Scope?.EntityVersion);
        if (scope.Mode is not ("general" or "entity")) return Results.BadRequest(new { error = "Scope mode must be general or entity." });
        var conversation = await chats.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null) return Results.NotFound();

        // Any subsequent turn closes the prior package; the model may prepare a revised package in this turn.
        await chats.DismissPendingProposalsAsync(conversationId, cancellationToken);
        var turns = await chats.GetRecentTurnsAsync(conversationId, 20, cancellationToken);
        var recent = turns.SelectMany(turn => new ModelMessage[]
        {
            new("system", $"Previous turn context: {FormatScope(turn.Scope)}; requested model {turn.RequestedModel}, actual model {turn.ActualModel}."),
            new("user", turn.UserText),
            new("assistant", turn.AssistantText)
        }).ToArray();
        var turnId = Guid.NewGuid();
        var requestedRoute = requestedModel == "DeepSeek" ? ChatModelRoute.ManualSelection : ChatModelRoute.Default;
        var result = await agent.RespondAsync(new AgentTurnRequest(conversationId, turnId, request.Message.Trim(), scope,
            requestedModel, requestedRoute, recent), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var turn = new ChatTurn(turnId, conversationId, request.Message.Trim(), result.Answer,
            new ChatTurnScope(result.Scope.Mode, result.Scope.EntityType, result.Scope.EntityId, result.Scope.EntityVersion),
            result.RequestedModel, result.ActualModel, result.ModelRoute, result.Sources, now, result.FallbackReason);
        ChatProposal? proposal = result.Proposal is null ? null : ToContract(result.Proposal);
        await transactions.ExecuteAsync(async ct =>
        {
            await chats.AppendTurnAsync(turn, ct);
            if (proposal is not null) await chats.SaveProposalAsync(proposal, ct);
        }, cancellationToken);

        try
        {
            var body = $"User: {turn.UserText}\n\nAssistant: {turn.AssistantText}";
            var chatContext = new SearchChatContext(turn.ConversationId, turn.Id, turn.Scope.Mode, turn.Scope.EntityType, turn.Scope.EntityId, turn.Scope.EntityVersion);
            await search.UpsertAsync(new SearchIndexSource("chat.turn", turn.Id, 1,
                string.IsNullOrWhiteSpace(conversation.Title) ? ShortTitle(turn.UserText) : conversation.Title!, body, null,
                $"/chat?conversationId={conversationId:D}&turnId={turnId:D}", turn.CreatedAtUtc, chatContext), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("PersonalDashboard.V2.Agent.SearchIndex").LogWarning(error,
                "Search indexing failed for completed chat turn {TurnId}; the stored chat turn remains available for feed rebuild.", turn.Id);
        }
        return Results.Ok(new { turn = ToTurnView(turn, proposal) });
    }

    private static async Task<IResult> ConfirmProposalAsync(Guid conversationId, Guid proposalId,
        IProposalConfirmationService confirmations, IChatConversationStore chats, CancellationToken cancellationToken)
    {
        var proposal = await chats.GetProposalAsync(proposalId, cancellationToken);
        if (proposal is null || proposal.ConversationId != conversationId) return Results.NotFound();
        var result = await confirmations.ConfirmAsync(proposalId, proposalId, cancellationToken);
        return result.Applied ? Results.Ok(new { result.Applied, result.AlreadyApplied })
            : Results.Conflict(new { result.Stale, result.Expired, result.Error, result.CurrentValues });
    }

    private static ChatProposal ToContract(ChangeProposal proposal)
    {
        var actions = proposal.Changes.Select(change => new ChatProposedAction(change.Id, change.Target.EntityType,
            change.Target.EntityId, change.Operation.ToString(), change.Target.ExpectedVersion,
            JsonDocument.Parse(change.AfterJson).RootElement.Clone(),
            $"{change.DisplayName}\n{change.Preview}",
            change.BeforeJson is null ? null : JsonDocument.Parse(change.BeforeJson).RootElement.Clone(),
            JsonDocument.Parse(change.AfterJson).RootElement.Clone())).ToArray();
        return new ChatProposal(proposal.Id, proposal.ConversationId, proposal.TurnId, actions,
            ChatProposalState.Pending, null, proposal.CreatedAt);
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
        proposalId = proposal?.Id,
        proposalStatus = proposal?.State.ToString(),
        changes = proposal?.Actions.Select(action =>
        {
            var split = action.Label.Split('\n', 2);
            return new { id = action.Id, operation = action.Operation, entityType = action.EntityType, entityId = action.EntityId,
                displayName = split[0], preview = split.Length > 1 ? split[1] : split[0], before = action.Before, after = action.After };
        })
    };

    private static string FormatScope(ChatTurnScope scope) => scope.Mode == "entity"
        ? $"{scope.EntityType}/{scope.EntityId} at version {scope.EntityVersion}" : "general";

    private static string ShortTitle(string text) => text.Length <= 72 ? text : text[..69] + "...";

    private sealed record SendTurnRequest(string Message, ScopeRequest? Scope, string RequestedModel);
    private sealed record ScopeRequest(string Mode, string? EntityType, Guid? EntityId, long? EntityVersion);
}
