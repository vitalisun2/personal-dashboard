using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Knowledge.Application;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

internal sealed class KnowledgeSyncMutationHandler(KnowledgeService knowledge) : ISyncMutationHandler
{
    public string Type => "knowledge.node";

    public async Task<SyncMutationResult> ApplyAsync(SyncOperation operation, CancellationToken cancellationToken = default)
    {
        try { return await knowledge.ApplySyncOperationAsync(operation, cancellationToken); }
        catch (PersonalDashboard.V2.Knowledge.Domain.KnowledgeConcurrentWriteException exception)
        { return new SyncMutationResult(false, null, exception.Message); }
    }
}
