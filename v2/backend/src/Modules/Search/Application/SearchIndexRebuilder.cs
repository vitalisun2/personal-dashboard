using PersonalDashboard.V2.Contracts.Search;

namespace PersonalDashboard.V2.Search.Application;

/// <summary>Callable reconciliation from authoritative module feeds into the Search index.</summary>
public sealed class SearchIndexRebuilder(IEnumerable<ISearchSourceFeed> feeds, ISearchIndexer indexer)
{
    private readonly ISearchSourceFeed[] _feeds = feeds.ToArray();
    public bool HasFeeds => _feeds.Length > 0;

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        foreach (var feed in _feeds)
        {
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await feed.ReadPageAsync(cursor, 250, cancellationToken);
                if (page is null) throw new InvalidOperationException("Search source feed returned a null page.");
                foreach (var change in page.Changes)
                {
                    if (change is null) throw new InvalidOperationException("Search source feed returned a null change.");
                    if (string.IsNullOrWhiteSpace(change.Kind) || change.Id == Guid.Empty || change.Version < 0)
                        throw new InvalidOperationException("Search source feed returned a malformed change.");
                    if (change.Deleted)
                        await indexer.DeleteAsync(change.Kind, change.Id, change.Version, cancellationToken);
                    else if (change.Source is { } source && source.Kind == change.Kind
                             && source.Id == change.Id && source.Version == change.Version)
                        await indexer.UpsertAsync(source, cancellationToken);
                    else
                        throw new InvalidOperationException("Search source feed returned a change without its matching active source.");
                }

                if (page.IsComplete) break;
                if (string.IsNullOrWhiteSpace(page.NextCursor) || !seenCursors.Add(page.NextCursor))
                    throw new InvalidOperationException("Search source feed did not advance its rebuild cursor.");
                cursor = page.NextCursor;
            } while (true);
        }
    }
}
