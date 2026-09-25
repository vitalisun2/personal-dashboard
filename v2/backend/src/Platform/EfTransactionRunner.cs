using PersonalDashboard.V2.Contracts.Transactions;

namespace PersonalDashboard.V2.Platform;

internal sealed class EfTransactionRunner(PlatformDbContext dbContext) : ITransactionRunner
{
    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async token =>
        {
            await operation(token);
            return true;
        }, cancellationToken);
    }

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
    }
}
