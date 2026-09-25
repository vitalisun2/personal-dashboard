using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Platform.Persistence;

namespace PersonalDashboard.V2.Platform;

internal sealed class EfEntityChangeJournal(PlatformDbContext dbContext) : IEntityChangeJournal
{
    public async Task<long> AppendAsync(EntitySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        // The row lock is held until the surrounding DbContext transaction commits.
        // Own a transaction when this is called outside a module write transaction.
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                ownedTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            }

            long sequence;
            var connection = dbContext.Database.GetDbConnection();
            var closeConnection = connection.State != ConnectionState.Open;
            try
            {
                if (closeConnection)
                {
                    await dbContext.Database.OpenConnectionAsync(cancellationToken);
                }

                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE platform.entity_change_cursor SET \"Sequence\" = \"Sequence\" + 1 WHERE \"Id\" = 1 RETURNING \"Sequence\"";
                command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
                sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (closeConnection)
                {
                    await dbContext.Database.CloseConnectionAsync();
                }
            }

            dbContext.EntityChanges.Add(new EntityChangeRow
            {
                Sequence = sequence,
                Type = snapshot.Type,
                Id = snapshot.Id,
                Version = snapshot.Version,
                Deleted = snapshot.Deleted,
                PayloadJson = snapshot.Payload?.GetRawText()
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return sequence;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    public async Task<EntityChangePage> ReadAfterAsync(long sequence, int pageSize, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, 500);
        var rows = await dbContext.EntityChanges.AsNoTracking()
            .Where(row => row.Sequence > sequence)
            .OrderBy(row => row.Sequence)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var isComplete = rows.Count <= size;
        if (!isComplete)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var changes = rows.Select(row => new EntityChange(
            row.Sequence,
            new EntitySnapshot(row.Type, row.Id, row.Version, row.Deleted, ParsePayload(row.PayloadJson))))
            .ToArray();

        return new EntityChangePage(changes, changes.Length == 0 ? sequence : changes[^1].Sequence, isComplete);
    }

    private static JsonElement? ParsePayload(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}
