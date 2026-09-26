using System.Text.Json;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Contracts.Transactions;
using Xunit;

namespace PersonalDashboard.V2.Agent.Tests;

public sealed class ProposalConfirmationTests
{
    [Fact]
    public async Task Model_supplied_display_label_and_preview_are_not_authoritative()
    {
        var toolArgs = JsonSerializer.Serialize(new
        {
            changes = new[]
            {
                new { module = "Knowledge", operation = "Create", entityType = "knowledge.document",
                    displayName = "Delete everything", preview = "Safe to confirm",
                    after = new { title = "New title", markdown = "new body" } }
            }
        });
        var service = new AgentTurnService(new FixedRouter(toolArgs), new EmptySearch(), new KnowledgeAccess([]), new EmptyPlanning(), new EmptyTasks());
        var response = await service.RespondAsync(new AgentTurnRequest(Guid.NewGuid(), Guid.NewGuid(), "Please edit the doc.",
            new AgentScope("general", null, null, null), "Gemma", PersonalDashboard.V2.Contracts.Chat.ChatModelRoute.Default, []));

        var change = Assert.Single(Assert.IsType<PersonalDashboard.V2.Agent.Domain.ChangeProposal>(response.Proposal).Changes);
        Assert.Equal("Документ знаний · New title", change.DisplayName);
        Assert.DoesNotContain("Delete everything", change.DisplayName);
        Assert.DoesNotContain("Safe to confirm", change.Preview);
        Assert.Contains("название", change.Preview);
        Assert.Contains("текст документа", change.Preview);
    }

    [Fact]
    public async Task Router_uses_only_Gemma_and_does_not_fallback_on_invalid_response()
    {
        var gemma = new Provider("Gemma", new ModelCompletion(null, [new ModelToolCall("1", "search_app", "not-json")]));
        var request = new ModelCompletionRequest([], [new ModelTool("search_app", "search", "{}")]);
        var router = new ChatModelRouter([gemma]);
        var unavailable = await Assert.ThrowsAsync<ChatModelUnavailableException>(() => router.CompleteAsync("Gemma", request, CancellationToken.None));
        Assert.IsAssignableFrom<JsonException>(unavailable.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CompleteAsync("DeepSeek", request, CancellationToken.None));
        Assert.Equal(1, gemma.Calls);
    }

    [Fact]
    public async Task Confirmation_rejects_multi_action_packages_and_same_confirmation_is_idempotent()
    {
        var store = new Store();
        var transactions = new RollbackRunner();
        var knowledge = new KnowledgeAccess(transactions.Writes);
        var service = new ProposalConfirmationService(store, transactions, knowledge, new EmptyPlanning(), new EmptyTasks());

        store.Proposal = Proposal([Action("first"), Action("second")]);
        var rejected = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());
        Assert.False(rejected.Applied);
        Assert.Empty(transactions.Writes);

        store.Proposal = Proposal([Action("safe")]);
        var confirmationId = Guid.NewGuid();
        var first = await service.ConfirmAsync(store.Proposal.Id, confirmationId);
        var repeat = await service.ConfirmAsync(store.Proposal.Id, confirmationId);
        Assert.True(first.Applied);
        Assert.True(repeat.AlreadyApplied);
        Assert.Single(transactions.Writes);
    }

    private static ChatProposal Proposal(IReadOnlyList<ChatProposedAction> actions) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actions, ChatProposalState.Pending, null, DateTimeOffset.UtcNow);

    private static ChatProposedAction Action(string title) => new(Guid.NewGuid(), "knowledge.document", Guid.NewGuid(),
        "Create", 1, JsonDocument.Parse($"{{\"title\":\"{title}\",\"markdown\":\"body\"}}").RootElement.Clone(), title, null, null);

    private sealed class Provider(string model, ModelCompletion? result) : IChatModelProvider
    {
        public string Model { get; } = model;
        public ModelCompletion? Result { get; set; } = result;
        public Exception? Error { get; set; }
        public int Calls { get; private set; }
        public Task<ModelCompletion> CompleteAsync(ModelCompletionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Task.FromResult(Result ?? throw new InvalidOperationException("no completion"));
        }
    }

    private sealed class RollbackRunner : ITransactionRunner
    {
        public List<Guid> Writes { get; } = [];
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            await ExecuteAsync(async ct => { await operation(ct); return true; }, cancellationToken);
        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            var before = Writes.Count;
            try { return await operation(cancellationToken); }
            catch { Writes.RemoveRange(before, Writes.Count - before); throw; }
        }
    }

    private sealed class Store : IChatConversationStore
    {
        public ChatProposal? Proposal { get; set; }
        public Task<ChatConversationState?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ChatConversationState?>(null);
        public Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatConversationPage> ListConversationsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatTurn>> GetRecentTurnsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatTurnPage> GetTurnsPageAsync(Guid conversationId, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AppendTurnAsync(ChatTurn turn, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Proposal?.Id == id ? Proposal : null);
        public Task<ChatProposal?> GetProposalForTurnAsync(Guid turnId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveProposalAsync(ChatProposal proposal, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> DismissPendingProposalsAsync(Guid conversationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> TryChangeProposalStateAsync(Guid id, ChatProposalState expectedState, ChatProposalState newState, Guid? confirmationId, CancellationToken cancellationToken = default)
        {
            if (Proposal?.Id != id || Proposal.State != expectedState) return Task.FromResult(false);
            Proposal = Proposal with { State = newState, ConfirmationId = confirmationId };
            return Task.FromResult(true);
        }
    }

    private sealed class KnowledgeAccess(List<Guid> writes) : IKnowledgeAgentAccess
    {
        public Guid? StaleId { get; set; }
        public KnowledgeNodeState? ReadState { get; set; }
        public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult(ReadState?.Id == id ? ReadState : null);
        public Task<KnowledgeMutationResult> ApplyAsync(KnowledgeMutation mutation, CancellationToken cancellationToken = default)
        {
            if (mutation.Id == StaleId) return Task.FromResult(new KnowledgeMutationResult(false, null, "stale"));
            writes.Add(mutation.Id);
            return Task.FromResult(new KnowledgeMutationResult(true, null, null));
        }
    }

    private sealed class EmptyPlanning : IPlanningAgentAccess
    {
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class EmptyTasks : ITasksAgentAccess
    {
        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FixedRouter(string arguments) : IChatModelRouter
    {
        public Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new RoutedCompletion(new ModelCompletion(null,
                [new ModelToolCall("call-1", "propose_changes", arguments)]), requestedModel, requestedModel, null));
    }

    private sealed class EmptySearch : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResponse([], null, true, null));
    }
}
