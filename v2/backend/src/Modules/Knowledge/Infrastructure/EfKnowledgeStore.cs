using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Knowledge.Domain;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

internal sealed class EfKnowledgeStore(PlatformDbContext dbContext, ITransactionRunner transactions) : IKnowledgeStore
{
    public Task<TResult> InTransactionAsync<TResult>(Func<IKnowledgeTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) =>
        ExecuteAsync(operation, cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(Func<IKnowledgeTransaction, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await transactions.ExecuteAsync(ct => operation(new KnowledgeTransaction(dbContext), ct), cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var id = exception.Entries.Select(entry => entry.Entity).OfType<KnowledgeNode>().FirstOrDefault()?.Id ?? Guid.Empty;
            throw new KnowledgeConcurrentWriteException(id);
        }
    }

    private sealed class KnowledgeTransaction(PlatformDbContext dbContext) : IKnowledgeTransaction
    {
        public async Task<IReadOnlyList<KnowledgeNode>> GetLiveNodesAsync(CancellationToken cancellationToken) =>
            await dbContext.Set<KnowledgeNode>()
                .Where(node => node.DeletedAt == null && node.ArchivedAt == null)
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<KnowledgeNode>> GetAllNodesAsync(CancellationToken cancellationToken) =>
            await dbContext.Set<KnowledgeNode>().ToListAsync(cancellationToken);

        public async Task AddAsync(KnowledgeNode node, CancellationToken cancellationToken) =>
            await dbContext.Set<KnowledgeNode>().AddAsync(node, cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
    }
}
