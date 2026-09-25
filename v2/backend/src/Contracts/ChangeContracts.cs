using System.Text.Json;

namespace PersonalDashboard.V2.Contracts.Changes;

public sealed record EntitySnapshot(string Type, Guid Id, long Version, bool Deleted, JsonElement? Payload);

public sealed record EntityChange(long Sequence, EntitySnapshot Snapshot);

public sealed record EntityChangePage(IReadOnlyList<EntityChange> Changes, long NextSequence, bool IsComplete);

public interface IEntityChangeJournal
{
    Task<long> AppendAsync(EntitySnapshot snapshot, CancellationToken cancellationToken = default);

    Task<EntityChangePage> ReadAfterAsync(long sequence, int pageSize, CancellationToken cancellationToken = default);
}
