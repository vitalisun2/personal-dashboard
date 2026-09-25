using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Application;

/// <summary>Full-rebuild projection consumed by the shared Search indexer.</summary>
public sealed class KnowledgeSearchSourceFeed(IKnowledgeStore store) : ISearchSourceFeed
{
    public Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) =>
        store.InTransactionAsync(async (tx, ct) =>
        {
            if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
            Guid? after = null;
            if (!string.IsNullOrWhiteSpace(cursor) && !Guid.TryParse(cursor, out var parsed))
                throw new ArgumentException("The Knowledge search cursor is invalid.", nameof(cursor));
            else if (!string.IsNullOrWhiteSpace(cursor)) after = Guid.Parse(cursor);

            var all = await tx.GetAllNodesAsync(ct);
            var ordered = all.Where(n => after is null || n.Id.CompareTo(after.Value) > 0)
                .OrderBy(n => n.Id).ToList();
            var batch = ordered.Take(pageSize).ToList();
            var changes = batch.Where(n => n.Type == KnowledgeNodeType.Document)
                .Select(n => new SearchSourceChange("knowledge.document", n.Id, n.Version, n.IsDeleted || n.IsArchived,
                    n.IsDeleted || n.IsArchived ? null : new SearchIndexSource("knowledge.document", n.Id, n.Version, n.Title,
                        n.Markdown, KnowledgeTree.GetPath(all, n), $"/knowledge/documents/{n.Id}", n.UpdatedAt)))
                .ToArray();
            var complete = ordered.Count <= pageSize;
            var next = complete || batch.Count == 0 ? null : batch[^1].Id.ToString("D");
            return new SearchSourcePage(changes, next, complete);
        }, cancellationToken);
}
