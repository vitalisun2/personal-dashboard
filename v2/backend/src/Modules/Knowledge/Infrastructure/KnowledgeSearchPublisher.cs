using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

/// <summary>Best-effort post-commit index updates; the source feed supports repair after failures.</summary>
internal sealed class KnowledgeSearchPublisher(IEnumerable<ISearchIndexer> indexers, ILogger<KnowledgeSearchPublisher> logger, PlatformDbContext dbContext)
    : IKnowledgeSearchPublisher
{
    public async Task PublishAsync(KnowledgeMutation mutation, CancellationToken cancellationToken)
    {
        var targets = indexers.ToArray();
        if (targets.Length == 0) return;
        if (dbContext.Database.CurrentTransaction is not null)
        {
            logger.LogDebug("Deferring Knowledge search indexing because the caller owns an outer database transaction.");
            return;
        }
        foreach (var node in mutation.ChangedNodes.Where(node => node.Kind == "document"))
        {
            foreach (var indexer in targets)
            {
                try
                {
                    if (node.Deleted || node.Archived)
                    {
                        await indexer.DeleteAsync("knowledge.document", node.Id, node.Version, cancellationToken);
                        continue;
                    }

                    await indexer.UpsertAsync(new SearchIndexSource("knowledge.document", node.Id, node.Version,
                        node.Title, node.Markdown, node.Path, $"/knowledge/documents/{node.Id}", node.UpdatedAt), cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Knowledge search indexing failed after committing document {DocumentId} version {Version}.", node.Id, node.Version);
                }
            }
        }
    }
}
