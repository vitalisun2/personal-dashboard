using System.Text.Json;
using PersonalDashboard.V2.Contracts.Changes;

namespace PersonalDashboard.V2.Contracts.Sync;

public enum SyncOperationKind
{
    Upsert,
    Delete
}

public sealed record SyncOperation(
    Guid OperationId,
    string Type,
    Guid Id,
    long? ExpectedVersion,
    SyncOperationKind Kind,
    JsonElement? Payload);

public sealed record SyncMutationResult(bool Applied, EntitySnapshot? Current, string? ConflictReason);

public interface ISyncMutationHandler
{
    string Type { get; }

    Task<SyncMutationResult> ApplyAsync(SyncOperation operation, CancellationToken cancellationToken = default);
}

public interface ISyncOperationJournal
{
    Task<SyncMutationResult?> FindResultAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task RecordResultAsync(Guid operationId, SyncMutationResult result, CancellationToken cancellationToken = default);
}
