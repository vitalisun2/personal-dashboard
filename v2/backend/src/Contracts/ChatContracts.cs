using System.Text.Json;
using PersonalDashboard.V2.Contracts.Search;

namespace PersonalDashboard.V2.Contracts.Chat;

public sealed record ChatConversationState(Guid Id, string? Title, DateTimeOffset CreatedAtUtc);

public sealed record ChatConversationSummary(Guid Id, string? Title, DateTimeOffset UpdatedAtUtc, long TurnCount);

public sealed record ChatConversationPage(IReadOnlyList<ChatConversationSummary> Conversations, string? NextCursor);

public sealed record ChatTurnScope(
    string Mode,
    string? EntityType,
    Guid? EntityId,
    long? EntityVersion);

public enum ChatModelRoute { Default, ManualSelection, AutomaticFallback }

public sealed record ChatTurn(
    Guid Id,
    Guid ConversationId,
    string UserText,
    string AssistantText,
    ChatTurnScope Scope,
    string RequestedModel,
    string ActualModel,
    ChatModelRoute ModelRoute,
    IReadOnlyList<SearchSourceReference> Sources,
    DateTimeOffset CreatedAtUtc);

public sealed record ChatTurnPage(IReadOnlyList<ChatTurn> Turns, string? NextCursor);

// Payload is the exact versioned action displayed in the preview, not model prose.
public sealed record ChatProposedAction(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Operation,
    long? ExpectedVersion,
    JsonElement Payload,
    string Label,
    JsonElement? Before,
    JsonElement? After);

public enum ChatProposalState { Pending, Applied, Dismissed }

public sealed record ChatProposal(
    Guid Id,
    Guid ConversationId,
    Guid TurnId,
    IReadOnlyList<ChatProposedAction> Actions,
    ChatProposalState State,
    Guid? ConfirmationId,
    DateTimeOffset CreatedAtUtc);

public interface IChatConversationStore
{
    Task<ChatConversationState?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default);

    Task<ChatConversationPage> ListConversationsAsync(
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatTurn>> GetRecentTurnsAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ChatTurnPage> GetTurnsPageAsync(
        Guid conversationId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task AppendTurnAsync(ChatTurn turn, CancellationToken cancellationToken = default);

    Task<ChatProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChatProposal?> GetProposalForTurnAsync(Guid turnId, CancellationToken cancellationToken = default);

    Task SaveProposalAsync(ChatProposal proposal, CancellationToken cancellationToken = default);

    Task<int> DismissPendingProposalsAsync(Guid conversationId, CancellationToken cancellationToken = default);

    // Call within ITransactionRunner so proposal state and domain writes commit together.
    Task<bool> TryChangeProposalStateAsync(
        Guid id,
        ChatProposalState expectedState,
        ChatProposalState newState,
        Guid? confirmationId,
        CancellationToken cancellationToken = default);
}
