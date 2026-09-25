using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Search.Infrastructure;
using PersonalDashboard.V2.Search.Domain;
using Xunit;

namespace PersonalDashboard.V2.Search.Tests;

public sealed class SearchCatchUpIntegrationTests
{
    [PgFact]
    public async Task NaturalRussianQueryFindsBothSourcesWithoutEmbeddingsAndContextOnlyFiltersChat()
    {
        var connectionString = Environment.GetEnvironmentVariable("V2_SEARCH_TEST_CONNECTION")!;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PersonalOsV2"] = connectionString,
            ["OLLAMA_URL"] = "http://127.0.0.1:11431",
            ["OLLAMA_EMBED_MODEL"] = "embeddinggemma"
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddPersonalOsV2Platform(configuration);
        collection.AddSearchInfrastructure(configuration);
        collection.AddSingleton<ISearchSourceFeed>(new EmptySearchSourceFeed());
        await using var provider = collection.BuildServiceProvider();
        await InitializeSearchSchemaAsync(provider);
        var indexer = provider.GetRequiredService<ISearchIndexer>();
        var now = DateTimeOffset.UtcNow;
        var sources = new[]
        {
            new SearchIndexSource("knowledge.document", firstId, 1, "Декор площади", "Фонари на площади",
                "/test/decor-one", "/test/decor-one", now),
            new SearchIndexSource("knowledge.document", secondId, 1, "Идеи оформления", "Городской декор и лавочки",
                "/test/decor-two", "/test/decor-two", now)
        };

        try
        {
            foreach (var source in sources) await indexer.UpsertAsync(source);
            Assert.Equal(new[] { "писал", "декор" }, SearchTerms.Significant("где я писал всё про декор"));
            var result = await provider.GetRequiredService<ISearchService>().SearchAsync(new SearchRequest(
                "где я писал всё про декор", SearchCoverageMode.Exhaustive,
                ["knowledge.document"], new SearchChatFilter(EntityType: "knowledge.document", EntityId: Guid.NewGuid())));
            Assert.Contains(result.Hits, hit => hit.Source.Id == firstId);
            Assert.Contains(result.Hits, hit => hit.Source.Id == secondId);
            Assert.All(result.Hits, hit => Assert.False(hit.Source.IsChatHistory));
            Assert.All(result.Hits.Where(hit => hit.Source.Id == firstId || hit.Source.Id == secondId),
                hit => Assert.Equal(SearchMatchKind.Lexical, hit.MatchKind));

            var relevant = await provider.GetRequiredService<ISearchService>().SearchAsync(new SearchRequest(
                "где я писал всё про декор", SearchCoverageMode.Relevant,
                ["knowledge.document"], new SearchChatFilter(EntityType: "knowledge.document", EntityId: Guid.NewGuid())));
            Assert.False(relevant.IsComplete);
            Assert.Contains("1000", relevant.CoverageNote, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var source in sources) await indexer.DeleteAsync(source.Kind, source.Id, source.Version);
        }
    }

    [PgFact]
    public async Task FailedFeedPassKeepsCursorThenReconcilesAndVersionedTombstoneBlocksResurrection()
    {
        var connectionString = Environment.GetEnvironmentVariable("V2_SEARCH_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var sourceId = Guid.NewGuid();
        const string kind = "test.search-journal";
        var source = new SearchIndexSource(kind, sourceId, 7, "Journal indexed source",
            "cursor reconciliation token", "/test/journal", "/test/journal", DateTimeOffset.UtcNow);
        var feed = new FailOnceFeed(new SearchSourceChange(kind, sourceId, 7, false, source));
        var journal = new OneChangeJournal(new EntityChange(1,
            new EntitySnapshot(kind, sourceId, 7, false, null)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PersonalOsV2"] = connectionString,
            ["OLLAMA_URL"] = "http://127.0.0.1:11431",
            ["OLLAMA_EMBED_MODEL"] = "embeddinggemma"
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddPersonalOsV2Platform(configuration);
        collection.AddSearchInfrastructure(configuration);
        collection.AddSingleton<ISearchSourceFeed>(feed);
        collection.RemoveAll<IEntityChangeJournal>();
        collection.AddSingleton<IEntityChangeJournal>(journal);
        await using var provider = collection.BuildServiceProvider();
        await InitializeSearchSchemaAsync(provider);
        await ResetTestRowsAsync(provider, kind, sourceId);

        var catchUp = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "SearchJournalCatchUpWorker");
        await catchUp.StartAsync(CancellationToken.None);
        try
        {
            await feed.RetryAttemptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await ReadCursorAsync(provider));
            feed.AllowSuccessfulPage.TrySetResult();
            await WaitForCursorAsync(provider, 1, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await catchUp.StopAsync(CancellationToken.None);
        }

        var indexer = provider.GetRequiredService<ISearchIndexer>();
        await indexer.DeleteAsync(kind, sourceId, 7);
        await indexer.UpsertAsync(source);
        await indexer.UpsertAsync(source with { Version = 6, Title = "Stale source" });
        Assert.Equal((7L, true, 0), await ReadSourceStateAsync(provider, kind, sourceId));

        await indexer.UpsertAsync(source with { Version = 8, Title = "Recovered source", Path = "/test/recovered" });
        Assert.Equal((8L, false, 1), await ReadSourceStateAsync(provider, kind, sourceId));
    }

    private static async Task InitializeSearchSchemaAsync(IServiceProvider provider)
    {
        var initializer = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "SearchSchemaInitializer");
        await initializer.StartAsync(CancellationToken.None);
    }

    private static async Task ResetTestRowsAsync(IServiceProvider provider, string kind, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var delete = db.Database.GetDbConnection().CreateCommand();
        delete.CommandText = "DELETE FROM search_sources WHERE kind = @kind AND id = @id; UPDATE search_catchup_cursor SET sequence = 0 WHERE id = 1;";
        Add(delete, "kind", kind);
        Add(delete, "id", id);
        await delete.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadCursorAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sequence FROM search_catchup_cursor WHERE id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForCursorAsync(IServiceProvider provider, long expected, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (await ReadCursorAsync(provider) == expected) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Search cursor did not reach {expected} within {timeout}.");
    }

    private static async Task<(long Version, bool Deleted, int Chunks)> ReadSourceStateAsync(
        IServiceProvider provider, string kind, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT source.version, source.is_deleted,
                   (SELECT count(*)::integer FROM search_chunks AS chunk WHERE chunk.kind = source.kind AND chunk.source_id = source.id)
              FROM search_sources AS source WHERE source.kind = @kind AND source.id = @id;
            """;
        Add(command, "kind", kind);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetBoolean(1), reader.GetInt32(2));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class FailOnceFeed(SearchSourceChange recovered) : ISearchSourceFeed
    {
        private int _calls;
        public TaskCompletionSource RetryAttemptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSuccessfulPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return new SearchSourcePage([recovered with { Source = null }], null, true);
            RetryAttemptEntered.TrySetResult();
            await AllowSuccessfulPage.Task.WaitAsync(cancellationToken);
            return new SearchSourcePage([recovered], null, true);
        }
    }

    private sealed class OneChangeJournal(EntityChange change) : IEntityChangeJournal
    {
        public Task<long> AppendAsync(EntitySnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EntityChangePage> ReadAfterAsync(long sequence, int pageSize, CancellationToken cancellationToken = default)
            => Task.FromResult(sequence < change.Sequence
                ? new EntityChangePage([change], change.Sequence, true)
                : new EntityChangePage([], sequence, true));
    }

    private sealed class EmptySearchSourceFeed : ISearchSourceFeed
    {
        public Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
            => Task.FromResult(new SearchSourcePage([], null, true));
    }

    private sealed class PgFactAttribute : FactAttribute
    {
        public PgFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("V2_SEARCH_TEST_CONNECTION")))
                Skip = "Set V2_SEARCH_TEST_CONNECTION to an isolated pgvector test database.";
        }
    }
}
