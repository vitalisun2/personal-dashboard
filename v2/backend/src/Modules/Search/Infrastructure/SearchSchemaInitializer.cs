using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Search.Infrastructure;

internal sealed class SearchSchemaInitializer(IServiceScopeFactory scopeFactory, ILogger<SearchSchemaInitializer> logger)
    : IHostedService
{
    private const string SchemaSql = """
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        CREATE TABLE IF NOT EXISTS search_sources (
            kind text NOT NULL,
            id uuid NOT NULL,
            version bigint NOT NULL,
            is_deleted boolean NOT NULL DEFAULT false,
            title text NOT NULL DEFAULT '',
            body text NOT NULL DEFAULT '',
            path text NULL,
            url text NULL,
            updated_at_utc timestamptz NOT NULL,
            chat_conversation_id uuid NULL,
            chat_turn_id uuid NULL,
            chat_mode text NULL,
            chat_entity_type text NULL,
            chat_entity_id uuid NULL,
            chat_entity_version bigint NULL,
            PRIMARY KEY (kind, id)
        );

        CREATE TABLE IF NOT EXISTS search_catchup_cursor (
            id integer PRIMARY KEY CHECK (id = 1),
            sequence bigint NOT NULL
        );
        INSERT INTO search_catchup_cursor (id, sequence) VALUES (1, 0)
        ON CONFLICT (id) DO NOTHING;

        CREATE TABLE IF NOT EXISTS search_chunks (
            kind text NOT NULL,
            source_id uuid NOT NULL,
            source_version bigint NOT NULL,
            chunk_index integer NOT NULL,
            chunk_text text NOT NULL,
            searchable_text text NOT NULL,
            search_vector tsvector GENERATED ALWAYS AS (to_tsvector('simple'::regconfig, searchable_text)) STORED,
            embedding vector NULL,
            embedding_model text NULL,
            PRIMARY KEY (kind, source_id, chunk_index),
            FOREIGN KEY (kind, source_id) REFERENCES search_sources(kind, id) ON DELETE CASCADE
        );

        ALTER TABLE search_chunks ADD COLUMN IF NOT EXISTS embedding_model text NULL;

        CREATE INDEX IF NOT EXISTS ix_search_sources_active_updated
            ON search_sources (updated_at_utc DESC) WHERE is_deleted = false;
        CREATE INDEX IF NOT EXISTS ix_search_sources_chat_context
            ON search_sources (chat_entity_type, chat_entity_id, chat_entity_version)
            WHERE kind = 'chat.turn' AND is_deleted = false;
        CREATE INDEX IF NOT EXISTS ix_search_chunks_fts
            ON search_chunks USING gin (search_vector);
        CREATE INDEX IF NOT EXISTS ix_search_chunks_substring
            ON search_chunks USING gin (searchable_text gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_search_chunks_pending_embeddings
            ON search_chunks (kind, source_id, chunk_index) WHERE embedding IS NULL;
        """;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.ExecuteSqlRawAsync(SchemaSql, cancellationToken);
        logger.LogInformation("Search PostgreSQL schema is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
