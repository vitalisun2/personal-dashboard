using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Search.Application;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Infrastructure;

internal sealed class PostgresSearchCandidateStore(
    PlatformDbContext db,
    OllamaEmbeddingClient embedder,
    IConfiguration configuration,
    ILogger<PostgresSearchCandidateStore> logger) : ISearchCandidateStore
{
    private const int RelevantCandidateLimit = 1000;
    private readonly double _minimumSimilarity = ParseSimilarity(configuration["V2_SEARCH_MIN_SEMANTIC_SIMILARITY"]);

    public async Task<SearchCandidateSet> FindCandidatesAsync(SearchCriteria criteria, SearchCoverageMode mode,
        CancellationToken cancellationToken = default)
    {
        float[]? queryEmbedding = null;
        string? coverageNote = null;
        try
        {
            queryEmbedding = (await embedder.EmbedAsync([criteria.Query], cancellationToken))[0];
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Query embedding failed; Search is falling back to lexical matches.");
            coverageNote = "Смысловой поиск временно недоступен; показаны текстовые совпадения.";
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        var candidates = await ReadCandidatesAsync(criteria, queryEmbedding, mode, cancellationToken);
        var embeddingsPending = await HasPendingEmbeddingsAsync(cancellationToken);
        if (embeddingsPending)
        {
            var pendingNote = "Смысловой индекс ещё обрабатывает источники; повторите поиск, чтобы проверить полный охват.";
            coverageNote = coverageNote is null ? pendingNote : $"{coverageNote} {pendingNote}";
        }

        var retrievalIsBounded = mode == SearchCoverageMode.Relevant;
        if (retrievalIsBounded)
        {
            const string relevantNote = "Релевантный режим ограничивает выборку 1000 фрагментами; выберите «Найти всё» для полного лексического охвата.";
            coverageNote = coverageNote is null ? relevantNote : $"{coverageNote} {relevantNote}";
        }

        return new SearchCandidateSet(candidates, !retrievalIsBounded && queryEmbedding is not null && !embeddingsPending,
            coverageNote);
    }

    private async Task<List<SearchCandidate>> ReadCandidatesAsync(SearchCriteria criteria, float[]? queryEmbedding,
        SearchCoverageMode mode, CancellationToken cancellationToken)
    {
        var hasEmbedding = queryEmbedding is not null;
        var semanticSelect = hasEmbedding
            ? "CASE WHEN chunk.embedding_model = @embedding_model AND chunk.embedding IS NOT NULL THEN 1 - (chunk.embedding <=> CAST(@embedding AS vector)) ELSE NULL END"
            : "NULL::double precision";
        var semanticMatch = hasEmbedding
            ? "OR (chunk.embedding_model = @embedding_model AND chunk.embedding IS NOT NULL AND 1 - (chunk.embedding <=> CAST(@embedding AS vector)) >= @minimum_similarity)"
            : string.Empty;
        var limit = mode == SearchCoverageMode.Exhaustive ? string.Empty : $"LIMIT {RelevantCandidateLimit}";
        var terms = SearchTerms.Significant(criteria.Query);
        var ftsQuery = string.Join(" | ", terms);
        var termPatterns = terms.Select(term => LiteralPattern(term)).ToArray();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"""
            SELECT source.kind, source.id, source.version, source.title, source.path, source.url,
                   source.updated_at_utc, source.chat_conversation_id, source.chat_turn_id,
                   source.chat_mode, source.chat_entity_type, source.chat_entity_id, source.chat_entity_version,
                   chunk.chunk_index, chunk.chunk_text,
                   ts_rank_cd(chunk.search_vector, to_tsquery('simple', @fts_query))::double precision,
                   {semanticSelect}
              FROM search_chunks AS chunk
              JOIN search_sources AS source ON source.kind = chunk.kind AND source.id = chunk.source_id
             WHERE source.is_deleted = false
               AND (cardinality(@kinds) = 0 OR source.kind = ANY(@kinds))
               AND (CAST(@after AS timestamptz) IS NULL OR source.updated_at_utc >= CAST(@after AS timestamptz))
               AND (CAST(@before AS timestamptz) IS NULL OR source.updated_at_utc <= CAST(@before AS timestamptz))
               AND (source.kind <> 'chat.turn' OR (
                    (CAST(@conversation AS uuid) IS NULL OR source.chat_conversation_id = CAST(@conversation AS uuid))
                AND (CAST(@mode AS text) IS NULL OR source.chat_mode = CAST(@mode AS text))
                AND (CAST(@entity_type AS text) IS NULL OR source.chat_entity_type = CAST(@entity_type AS text))
                AND (CAST(@entity_id AS uuid) IS NULL OR source.chat_entity_id = CAST(@entity_id AS uuid))
                AND (CAST(@entity_version AS bigint) IS NULL OR source.chat_entity_version = CAST(@entity_version AS bigint))))
               AND (
                   chunk.searchable_text ILIKE @pattern ESCAPE '\'
                   OR chunk.searchable_text ILIKE ANY(CAST(@term_patterns AS text[]))
                   OR chunk.search_vector @@ to_tsquery('simple', @fts_query)
                   {semanticMatch})
             ORDER BY (chunk.searchable_text ILIKE @pattern ESCAPE '\') DESC,
                      {semanticSelect} DESC NULLS LAST,
                      ts_rank_cd(chunk.search_vector, to_tsquery('simple', @fts_query)) DESC,
                      source.kind, source.id, chunk.chunk_index
             {limit};
            """;

        Add(command, "query", criteria.Query);
        Add(command, "pattern", LiteralPattern(criteria.Query));
        Add(command, "term_patterns", termPatterns);
        Add(command, "fts_query", ftsQuery);
        Add(command, "kinds", criteria.Kinds?.ToArray() ?? []);
        Add(command, "after", criteria.UpdatedAfterUtc);
        Add(command, "before", criteria.UpdatedBeforeUtc);
        Add(command, "conversation", criteria.Context?.ConversationId);
        Add(command, "mode", criteria.Context?.Mode);
        Add(command, "entity_type", criteria.Context?.EntityType);
        Add(command, "entity_id", criteria.Context?.EntityId);
        Add(command, "entity_version", criteria.Context?.EntityVersion);
        if (hasEmbedding)
        {
            Add(command, "embedding", OllamaEmbeddingClient.ToVectorLiteral(queryEmbedding!));
            Add(command, "embedding_model", embedder.ModelName);
            Add(command, "minimum_similarity", _minimumSimilarity);
        }

        var candidates = new List<SearchCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chatContext = reader.IsDBNull(7) || reader.IsDBNull(8) ? null : new IndexedChatContext(
                reader.GetGuid(7), reader.GetGuid(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12));
            var source = new IndexedSource(reader.GetString(0), reader.GetGuid(1), reader.GetInt64(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), ReadUtc(reader.GetValue(6)), chatContext);
            candidates.Add(new SearchCandidate(source, reader.GetInt32(13), reader.GetString(14),
                reader.IsDBNull(16) ? null : Convert.ToDouble(reader.GetValue(16), CultureInfo.InvariantCulture),
                reader.IsDBNull(15) ? null : Convert.ToDouble(reader.GetValue(15), CultureInfo.InvariantCulture)));
        }

        return candidates;
    }

    private async Task<bool> HasPendingEmbeddingsAsync(CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM search_chunks AS chunk
                JOIN search_sources AS source ON source.kind = chunk.kind AND source.id = chunk.source_id
                 AND source.version = chunk.source_version AND source.is_deleted = false
                WHERE chunk.embedding IS NULL OR chunk.embedding_model IS DISTINCT FROM @model);
            """;
        Add(command, "model", embedder.ModelName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static string LiteralPattern(string query) =>
        "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static DateTimeOffset ReadUtc(object value) => value switch
    {
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => throw new InvalidCastException("Unexpected UTC timestamp type from PostgreSQL.")
    };

    private static double ParseSimilarity(string? configured)
    {
        if (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0.3;
        return Math.Clamp(value, -1, 1);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
