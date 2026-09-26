using System.Text.Json;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Knowledge;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Transactions;

namespace PersonalDashboard.V2.Agent.Application;

public sealed record ProposalConfirmationResult(bool Applied, bool AlreadyApplied, bool Stale, bool Expired, string? Error, IReadOnlyList<object?> CurrentValues);
public sealed record ProposalActionCurrent(Guid ActionId, string EntityType, Guid EntityId, string Label, object? Current, JsonElement? Proposed);

public interface IProposalConfirmationService
{
    Task<ProposalConfirmationResult> ConfirmAsync(Guid proposalId, Guid confirmationId, CancellationToken cancellationToken = default);
}

public sealed class ProposalConfirmationService(
    IChatConversationStore chats,
    ITransactionRunner transactions,
    IKnowledgeAgentAccess knowledge,
    IPlanningAgentAccess planning,
    ITasksAgentAccess tasks) : IProposalConfirmationService
{
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(10);

    public async Task<ProposalConfirmationResult> ConfirmAsync(Guid proposalId, Guid confirmationId, CancellationToken cancellationToken = default)
    {
        if (proposalId == Guid.Empty || confirmationId == Guid.Empty)
            throw new ArgumentException("Proposal and confirmation IDs are required.");
        var proposal = await chats.GetProposalAsync(proposalId, cancellationToken);
        if (proposal is null) return new(false, false, false, false, "Proposal not found.", []);
        if (proposal.State == ChatProposalState.Applied)
            return new(proposal.ConfirmationId == confirmationId, proposal.ConfirmationId == confirmationId, false, false,
                proposal.ConfirmationId == confirmationId ? null : "Proposal was already confirmed with a different confirmation ID.", []);
        if (proposal.State != ChatProposalState.Pending)
            return new(false, false, false, false, "Proposal is no longer pending.", []);
        if (proposal.Actions.Count != 1 || proposal.Actions.Any(action =>
                !string.Equals(action.Operation, nameof(ChangeOperation.Create), StringComparison.OrdinalIgnoreCase) || action.EntityType is not ("knowledge.document" or "tasks.task")))
            return new(false, false, false, false, "Only creating one new knowledge document or task is allowed.", []);
        if (DateTimeOffset.UtcNow - proposal.CreatedAtUtc >= ProposalLifetime)
        {
            await chats.TryChangeProposalStateAsync(proposal.Id, ChatProposalState.Pending, ChatProposalState.Dismissed, null, cancellationToken);
            return new(false, false, false, true, "Proposal expired. Prepare a new preview.", []);
        }

        try
        {
            return await transactions.ExecuteAsync(async ct =>
            {
                var current = await chats.GetProposalAsync(proposalId, ct);
                if (current is null) return new ProposalConfirmationResult(false, false, false, false, "Proposal not found.", []);
                if (current.State == ChatProposalState.Applied)
                    return current.ConfirmationId == confirmationId
                        ? new ProposalConfirmationResult(true, true, false, false, null, [])
                        : new ProposalConfirmationResult(false, false, false, false, "Proposal was already confirmed with a different confirmation ID.", []);
                if (current.State != ChatProposalState.Pending)
                    return new ProposalConfirmationResult(false, false, false, false, "Proposal is no longer pending.", []);
                if (DateTimeOffset.UtcNow - current.CreatedAtUtc >= ProposalLifetime)
                    throw new ProposalConflictException("Proposal expired. Prepare a new preview.", null, isExpired: true);

                var freshValues = new List<object?>();
                foreach (var action in current.Actions)
                {
                    MutationResult result;
                    try { result = await ApplyAsync(action, ct); }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or JsonException)
                    {
                        throw new ProposalConflictException(error.Message, null);
                    }
                    freshValues.Add(result.Current);
                    if (!result.Applied) throw new ProposalConflictException(result.ConflictReason ?? "An object changed after preview.", result.Current);
                }
                if (!await chats.TryChangeProposalStateAsync(proposalId, ChatProposalState.Pending, ChatProposalState.Applied, confirmationId, ct))
                    throw new ProposalConflictException("Proposal state changed while confirming.", null);
                return new ProposalConfirmationResult(true, false, false, false, null, freshValues);
            }, cancellationToken);
        }
        catch (ProposalConflictException conflict)
        {
            var finalState = await chats.GetProposalAsync(proposalId, cancellationToken);
            if (finalState?.State == ChatProposalState.Applied)
                return finalState.ConfirmationId == confirmationId
                    ? new(true, true, false, false, null, [])
                    : new(false, false, false, false, "Proposal was already confirmed with a different confirmation ID.", []);
            await chats.TryChangeProposalStateAsync(proposalId, ChatProposalState.Pending, ChatProposalState.Dismissed, null, cancellationToken);
            var currentPreviews = new List<object?>();
            foreach (var action in proposal.Actions)
                currentPreviews.Add(new ProposalActionCurrent(action.Id, action.EntityType, action.EntityId, action.Label,
                    await ReadCurrentAsync(action, cancellationToken), action.After));
            return new(false, false, !conflict.IsExpired, conflict.IsExpired, conflict.Message,
                currentPreviews);
        }
    }

    private async Task<MutationResult> ApplyAsync(ChatProposedAction action, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(action.Payload.GetRawText());
        var operation = Enum.Parse<ChangeOperation>(action.Operation, true);
        if (action.EntityType.StartsWith("knowledge.", StringComparison.Ordinal))
        {
            var kind = action.EntityType == "knowledge.document" ? KnowledgeNodeKind.Document
                : action.EntityType == "knowledge.section" ? KnowledgeNodeKind.Section
                : throw new InvalidDataException("Unsupported Knowledge entity type.");
            var result = await knowledge.ApplyAsync(new KnowledgeMutation(
                Enum.Parse<KnowledgeMutationKind>(operation.ToString(), true), kind, action.EntityId, action.ExpectedVersion,
                GuidValue(payload.RootElement, "parentSectionId"), StringValue(payload.RootElement, "title"), StringValue(payload.RootElement, "markdown"),
                OrderValue(payload.RootElement)), cancellationToken);
            return new(result.Applied, result.Current, result.ConflictReason);
        }
        if (action.EntityType.StartsWith("planning.", StringComparison.Ordinal))
        {
            var kind = action.EntityType switch
            {
                "planning.project" => PlanningEntityKind.Project,
                "planning.milestone" => PlanningEntityKind.Milestone,
                "planning.feature" => PlanningEntityKind.Feature,
                _ => throw new InvalidDataException("Unsupported Planning entity type.")
            };
            var mutation = new PlanningMutation(
                Enum.Parse<PlanningMutationKind>(operation == ChangeOperation.SetFeatureStatus ? "SetFeatureStatus" : operation.ToString(), true),
                kind, action.EntityId, GuidValue(payload.RootElement, "projectId"), GuidValue(payload.RootElement, "milestoneId"), action.ExpectedVersion,
                StringValue(payload.RootElement, "title"), StringValue(payload.RootElement, "description"), StringValue(payload.RootElement, "featureStatus"),
                OrderValue(payload.RootElement), LongValue(payload.RootElement, "expectedParentVersion"));
            var result = await planning.ApplyAsync(mutation, cancellationToken);
            return new(result.Applied, result.Current, result.ConflictReason);
        }
        if (action.EntityType.StartsWith("tasks.", StringComparison.Ordinal))
        {
            var kind = action.EntityType switch
            {
                "tasks.task" => TaskEntityKind.Task,
                "tasks.section" => TaskEntityKind.Section,
                _ => throw new InvalidDataException("Unsupported Tasks entity type.")
            };
            var mutation = new TaskMutation(
                Enum.Parse<TaskMutationKind>(operation.ToString(), true), kind, action.EntityId, action.ExpectedVersion,
                StringValue(payload.RootElement, "title"), StringValue(payload.RootElement, "description"), PlanningLinkValue(payload.RootElement),
                StringValue(payload.RootElement, "placement"), StringValue(payload.RootElement, "workStatus"), GuidValue(payload.RootElement, "sectionId"),
                StringValue(payload.RootElement, "bucket"), OrderValue(payload.RootElement));
            var result = await tasks.ApplyAsync(mutation, cancellationToken);
            return new(result.Applied, result.Current, result.ConflictReason);
        }
        throw new InvalidDataException($"Unsupported entity type '{action.EntityType}'.");
    }

    private Task<object?> ReadCurrentAsync(ChatProposedAction action, CancellationToken cancellationToken) => action.EntityType switch
    {
        "knowledge.document" => ReadKnowledge(KnowledgeNodeKind.Document, action.EntityId, cancellationToken),
        "knowledge.section" => ReadKnowledge(KnowledgeNodeKind.Section, action.EntityId, cancellationToken),
        "planning.project" => ReadPlanning(PlanningEntityKind.Project, action.EntityId, cancellationToken),
        "planning.milestone" => ReadPlanning(PlanningEntityKind.Milestone, action.EntityId, cancellationToken),
        "planning.feature" => ReadPlanning(PlanningEntityKind.Feature, action.EntityId, cancellationToken),
        "tasks.task" => ReadTask(TaskEntityKind.Task, action.EntityId, cancellationToken),
        "tasks.section" => ReadTask(TaskEntityKind.Section, action.EntityId, cancellationToken),
        _ => Task.FromResult<object?>(null)
    };

    private async Task<object?> ReadKnowledge(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken) => await knowledge.ReadAsync(kind, id, cancellationToken);
    private async Task<object?> ReadPlanning(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken) => await planning.ReadAsync(kind, id, cancellationToken);
    private async Task<object?> ReadTask(TaskEntityKind kind, Guid id, CancellationToken cancellationToken) => await tasks.ReadAsync(kind, id, cancellationToken);

    private static string? StringValue(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static Guid? GuidValue(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetGuid() : null;

    private static long? LongValue(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static IReadOnlyList<VersionedEntityId>? OrderValue(JsonElement json)
    {
        if (!json.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Array) return null;
        return order.EnumerateArray().Select(x => new VersionedEntityId(x.GetProperty("id").GetGuid(), x.GetProperty("expectedVersion").GetInt64())).ToArray();
    }

    private static PlanningLink? PlanningLinkValue(JsonElement json)
    {
        if (!json.TryGetProperty("planning", out var planning) || planning.ValueKind == JsonValueKind.Null) return null;
        return new PlanningLink(GuidValue(planning, "projectId"), GuidValue(planning, "milestoneId"), GuidValue(planning, "featureId"));
    }

    private sealed record MutationResult(bool Applied, object? Current, string? ConflictReason);

    private sealed class ProposalConflictException(string message, object? current, bool isExpired = false) : Exception(message)
    {
        public object? Current { get; } = current;
        public bool IsExpired { get; } = isExpired;
    }
}
