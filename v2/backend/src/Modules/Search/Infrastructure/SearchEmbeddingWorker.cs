using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Search.Infrastructure;

internal sealed class SearchEmbeddingWorker(IServiceScopeFactory scopeFactory, ILogger<SearchEmbeddingWorker> logger)
    : BackgroundService
{
    private const int BatchSize = 12;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updated = await ProcessBatchAsync(stoppingToken);
                retryDelay = TimeSpan.FromSeconds(2);
                if (updated == 0) await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Search embedding batch failed; lexical search remains available.");
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, retryDelay.TotalSeconds * 2));
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<OllamaEmbeddingClient>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        var pending = await ReadPendingAsync(db, embedder.ModelName, cancellationToken);
        await db.Database.CloseConnectionAsync();
        if (pending.Count == 0) return 0;

        var vectors = await embedder.EmbedAsync(pending.Select(item => item.SearchableText).ToArray(), cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        for (var index = 0; index < pending.Count; index++)
        {
            var item = pending[index];
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                UPDATE search_chunks AS chunk
                   SET embedding = CAST(@embedding AS vector), embedding_model = @model
                 WHERE chunk.kind = @kind AND chunk.source_id = @id AND chunk.source_version = @version
                   AND chunk.chunk_index = @index AND chunk.searchable_text = @searchable
                   AND (chunk.embedding IS NULL OR chunk.embedding_model IS DISTINCT FROM @model)
                   AND EXISTS (
                       SELECT 1 FROM search_sources AS source
                        WHERE source.kind = chunk.kind AND source.id = chunk.source_id
                          AND source.version = chunk.source_version AND source.is_deleted = false);
                """;
            Add(command, "embedding", OllamaEmbeddingClient.ToVectorLiteral(vectors[index]));
            Add(command, "model", embedder.ModelName);
            Add(command, "kind", item.Kind);
            Add(command, "id", item.Id);
            Add(command, "version", item.Version);
            Add(command, "index", item.ChunkIndex);
            Add(command, "searchable", item.SearchableText);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return pending.Count;
    }

    private static async Task<List<PendingEmbedding>> ReadPendingAsync(PlatformDbContext db, string model,
        CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT chunk.kind, chunk.source_id, chunk.source_version, chunk.chunk_index, chunk.searchable_text
              FROM search_chunks AS chunk
              JOIN search_sources AS source
                ON source.kind = chunk.kind AND source.id = chunk.source_id
               AND source.version = chunk.source_version AND source.is_deleted = false
             WHERE chunk.embedding IS NULL OR chunk.embedding_model IS DISTINCT FROM @model
             ORDER BY source.updated_at_utc DESC, chunk.kind, chunk.source_id, chunk.chunk_index
             LIMIT @limit;
            """;
        Add(command, "limit", BatchSize);
        Add(command, "model", model);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PendingEmbedding>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new PendingEmbedding(reader.GetString(0), reader.GetGuid(1), reader.GetInt64(2),
                reader.GetInt32(3), reader.GetString(4)));
        return rows;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record PendingEmbedding(string Kind, Guid Id, long Version, int ChunkIndex, string SearchableText);
}
