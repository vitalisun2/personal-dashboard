using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Platform.Persistence;

namespace PersonalDashboard.V2.Platform;

internal sealed class EfSyncOperationJournal(PlatformDbContext dbContext) : ISyncOperationJournal
{
    public async Task<SyncMutationResult?> FindResultAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var json = await dbContext.SyncOperationResults.AsNoTracking()
            .Where(row => row.OperationId == operationId)
            .Select(row => row.ResultJson)
            .SingleOrDefaultAsync(cancellationToken);

        return json is null ? null : JsonSerializer.Deserialize<SyncMutationResult>(json);
    }

    public async Task RecordResultAsync(Guid operationId, SyncMutationResult result, CancellationToken cancellationToken = default)
    {
        dbContext.SyncOperationResults.Add(new SyncOperationResultRow
        {
            OperationId = operationId,
            ResultJson = JsonSerializer.Serialize(result)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
