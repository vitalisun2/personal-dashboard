using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Contracts.Transactions;

internal static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v2/sync");
        group.MapPost("/push", PushAsync);
        group.MapGet("/changes", ReadChangesAsync);
        return endpoints;
    }

    private static async Task<IResult> PushAsync(
        SyncPushRequest request,
        IEnumerable<ISyncMutationHandler> handlers,
        ISyncOperationJournal operationJournal,
        ITransactionRunner transactionRunner,
        CancellationToken cancellationToken)
    {
        if (request.Operations is null)
        {
            return Results.BadRequest(new { error = "Operations are required." });
        }

        var handlerMap = handlers.ToDictionary(handler => handler.Type, StringComparer.OrdinalIgnoreCase);
        var results = new List<SyncPushResult>(request.Operations.Count);
        var stop = false;

        foreach (var operation in request.Operations)
        {
            if (stop)
            {
                results.Add(new SyncPushResult(operation.OperationId, false, true, null, "A previous operation in this batch needs resolution."));
                continue;
            }

            if (await operationJournal.FindResultAsync(operation.OperationId, cancellationToken) is { } previous)
            {
                var prior = ToPushResult(operation.OperationId, previous);
                results.Add(prior);
                if (!prior.Applied)
                {
                    stop = true;
                }

                continue;
            }

            if (!handlerMap.TryGetValue(operation.Type, out var handler))
            {
                var unsupported = new SyncMutationResult(false, null, $"Unsupported entity type '{operation.Type}'.");
                var storedUnsupported = await transactionRunner.ExecuteAsync(async token =>
                {
                    var repeated = await operationJournal.FindResultAsync(operation.OperationId, token);
                    if (repeated is not null)
                    {
                        return repeated;
                    }

                    await operationJournal.RecordResultAsync(operation.OperationId, unsupported, token);
                    return unsupported;
                }, cancellationToken);
                results.Add(ToPushResult(operation.OperationId, storedUnsupported));
                stop = true;
                continue;
            }

            SyncMutationResult mutation;
            try
            {
                mutation = await transactionRunner.ExecuteAsync(async token =>
                {
                    var repeated = await operationJournal.FindResultAsync(operation.OperationId, token);
                    if (repeated is not null)
                    {
                        return repeated;
                    }

                    var applied = await handler.ApplyAsync(operation, token);
                    if (!applied.Applied)
                    {
                        throw new SyncOperationRejectedException(applied);
                    }

                    await operationJournal.RecordResultAsync(operation.OperationId, applied, token);
                    return applied;
                }, cancellationToken);
            }
            catch (SyncOperationRejectedException rejected)
            {
                // The handler's transaction was rolled back before persisting the stable rejection result.
                mutation = await transactionRunner.ExecuteAsync(async token =>
                {
                    var repeated = await operationJournal.FindResultAsync(operation.OperationId, token);
                    if (repeated is not null)
                    {
                        return repeated;
                    }

                    await operationJournal.RecordResultAsync(operation.OperationId, rejected.Result, token);
                    return rejected.Result;
                }, cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent request with the same id may have committed first.
                var repeated = await operationJournal.FindResultAsync(operation.OperationId, cancellationToken);
                if (repeated is null)
                {
                    throw;
                }

                mutation = repeated;
            }

            var result = ToPushResult(operation.OperationId, mutation);
            results.Add(result);
            if (!result.Applied)
            {
                stop = true;
            }
        }

        return Results.Ok(new SyncPushResponse(results));
    }

    private static async Task<Ok<EntityChangePage>> ReadChangesAsync(
        long? after,
        int? pageSize,
        IEntityChangeJournal changeJournal,
        CancellationToken cancellationToken)
    {
        var page = await changeJournal.ReadAfterAsync(Math.Max(0, after ?? 0), pageSize ?? 100, cancellationToken);
        return TypedResults.Ok(page);
    }

    private static SyncPushResult ToPushResult(Guid operationId, SyncMutationResult result) =>
        new(operationId, result.Applied, false, result.Current, result.ConflictReason);
}

internal sealed record SyncPushRequest(IReadOnlyList<SyncOperation>? Operations);

internal sealed record SyncPushResponse(IReadOnlyList<SyncPushResult> Results);

internal sealed record SyncPushResult(
    Guid OperationId,
    bool Applied,
    bool Skipped,
    EntitySnapshot? Current,
    string? ConflictReason);

internal sealed class SyncOperationRejectedException(SyncMutationResult result) : Exception
{
    public SyncMutationResult Result { get; } = result;
}
