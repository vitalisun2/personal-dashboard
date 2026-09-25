using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Sync;

namespace PersonalDashboard.V2.Planning.Infrastructure;

public abstract class PlanningSyncHandler(string type, PlanningEntityKind entityKind, IPlanningAgentAccess access) : ISyncMutationHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public string Type { get; } = type;

    public async Task<SyncMutationResult> ApplyAsync(SyncOperation operation, CancellationToken ct = default)
    {
        if (operation.Id == Guid.Empty) return new(false, null, "Entity ID is required.");
        if (operation.Payload is null) return new(false, null, "Planning sync payload is required.");
        PlanningMutation? input;
        try { input = JsonSerializer.Deserialize<PlanningMutation>(operation.Payload.Value.GetRawText(), JsonOptions); }
        catch (JsonException ex) { return new(false, null, $"Invalid planning payload: {ex.Message}"); }
        if (input is null || input.Id != operation.Id || input.Kind != entityKind) return new(false, null, "Planning payload type or ID does not match the operation.");
        if (operation.Kind == SyncOperationKind.Delete)
        {
            if (input.Operation != PlanningMutationKind.Delete) return new(false, null, "Delete requires a delete mutation payload.");
            input = input with { ExpectedVersion = operation.ExpectedVersion };
        }
        else if (operation.Kind == SyncOperationKind.Upsert)
        {
            if (input.Operation == PlanningMutationKind.Delete) return new(false, null, "Use kind=delete for a delete mutation.");
            if (input.Operation == PlanningMutationKind.Create && operation.ExpectedVersion is not null) return new(false, null, "Create must not have an expected entity version.");
            if (input.Operation != PlanningMutationKind.Create && operation.ExpectedVersion is null) return new(false, null, "Update requires an expected entity version.");
            input = input with { ExpectedVersion = operation.ExpectedVersion };
        }
        else return new(false, null, "Unsupported sync operation kind.");

        var result = await access.ApplyAsync(input, ct);
        if (!result.Applied) return new(false, result.Current is null ? null : Snapshot(result.Current), result.ConflictReason);
        if (operation.Kind == SyncOperationKind.Delete)
        {
            var tombstone = new EntitySnapshot(Type, operation.Id, (operation.ExpectedVersion ?? 0) + 1, true, null);
            return new(true, tombstone, null);
        }
        var current = await access.ReadAsync(entityKind, operation.Id, ct);
        return current is null
            ? new(false, null, "The mutation applied but its entity could not be read back.")
            : new(true, Snapshot(current), null);
    }

    private EntitySnapshot Snapshot(PlanningEntityState state) =>
        new(Type, state.Id, state.Version, false, JsonSerializer.SerializeToElement(state));
}

public sealed class PlanningProjectSyncHandler(IPlanningAgentAccess access) : PlanningSyncHandler("planning.project", PlanningEntityKind.Project, access);
public sealed class PlanningMilestoneSyncHandler(IPlanningAgentAccess access) : PlanningSyncHandler("planning.milestone", PlanningEntityKind.Milestone, access);
public sealed class PlanningFeatureSyncHandler(IPlanningAgentAccess access) : PlanningSyncHandler("planning.feature", PlanningEntityKind.Feature, access);
