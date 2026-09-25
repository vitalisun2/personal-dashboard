using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Infrastructure;

internal sealed class PostgresSearchIndexer(PlatformDbContext db) : ISearchIndexer
{
    public async Task UpsertAsync(SearchIndexSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Kind) || source.Id == Guid.Empty || source.Version < 0)
            throw new ArgumentException("Search source must have a kind, nonempty ID, and nonnegative version.", nameof(source));

        var chunks = SearchChunker.Split(source.Body);
        if (chunks.Count == 0) chunks = [string.Empty];
        var searchable = chunks.Select(chunk => string.Join('\n', source.Title, source.Path ?? string.Empty, chunk)).ToArray();

        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var accepted = await ExecuteReturningIdAsync("""
            INSERT INTO search_sources (
                kind, id, version, is_deleted, title, body, path, url, updated_at_utc,
                chat_conversation_id, chat_turn_id, chat_mode, chat_entity_type, chat_entity_id, chat_entity_version)
            VALUES (
                @kind, @id, @version, false, @title, @body, @path, @url, @updated,
                @conversation, @turn, @mode, @entity_type, @entity_id, @entity_version)
            ON CONFLICT (kind, id) DO UPDATE SET
                version = EXCLUDED.version,
                is_deleted = false,
                title = EXCLUDED.title,
                body = EXCLUDED.body,
                path = EXCLUDED.path,
                url = EXCLUDED.url,
                updated_at_utc = EXCLUDED.updated_at_utc,
                chat_conversation_id = EXCLUDED.chat_conversation_id,
                chat_turn_id = EXCLUDED.chat_turn_id,
                chat_mode = EXCLUDED.chat_mode,
                chat_entity_type = EXCLUDED.chat_entity_type,
                chat_entity_id = EXCLUDED.chat_entity_id,
                chat_entity_version = EXCLUDED.chat_entity_version
            WHERE search_sources.version < EXCLUDED.version
               OR (search_sources.version = EXCLUDED.version AND search_sources.is_deleted = false AND (
                    search_sources.title IS DISTINCT FROM EXCLUDED.title
                 OR search_sources.body IS DISTINCT FROM EXCLUDED.body
                 OR search_sources.path IS DISTINCT FROM EXCLUDED.path
                 OR search_sources.url IS DISTINCT FROM EXCLUDED.url
                 OR search_sources.updated_at_utc IS DISTINCT FROM EXCLUDED.updated_at_utc
                 OR search_sources.chat_conversation_id IS DISTINCT FROM EXCLUDED.chat_conversation_id
                 OR search_sources.chat_turn_id IS DISTINCT FROM EXCLUDED.chat_turn_id
                 OR search_sources.chat_mode IS DISTINCT FROM EXCLUDED.chat_mode
                 OR search_sources.chat_entity_type IS DISTINCT FROM EXCLUDED.chat_entity_type
                 OR search_sources.chat_entity_id IS DISTINCT FROM EXCLUDED.chat_entity_id
                 OR search_sources.chat_entity_version IS DISTINCT FROM EXCLUDED.chat_entity_version))
            RETURNING id;
            """, command =>
        {
            Add(command, "kind", source.Kind);
            Add(command, "id", source.Id);
            Add(command, "version", source.Version);
            Add(command, "title", source.Title);
            Add(command, "body", source.Body);
            Add(command, "path", source.Path);
            Add(command, "url", source.Url);
            Add(command, "updated", source.UpdatedAtUtc);
            Add(command, "conversation", source.ChatContext?.ConversationId);
            Add(command, "turn", source.ChatContext?.TurnId);
            Add(command, "mode", source.ChatContext?.Mode);
            Add(command, "entity_type", source.ChatContext?.EntityType);
            Add(command, "entity_id", source.ChatContext?.EntityId);
            Add(command, "entity_version", source.ChatContext?.EntityVersion);
        }, cancellationToken);

        if (accepted)
        {
            await ExecuteAsync("DELETE FROM search_chunks WHERE kind = @kind AND source_id = @id;", command =>
            {
                Add(command, "kind", source.Kind);
                Add(command, "id", source.Id);
            }, cancellationToken);

            for (var index = 0; index < chunks.Count; index++)
            {
                await ExecuteAsync("""
                    INSERT INTO search_chunks (kind, source_id, source_version, chunk_index, chunk_text, searchable_text)
                    VALUES (@kind, @id, @version, @index, @text, @searchable);
                    """, command =>
                {
                    Add(command, "kind", source.Kind);
                    Add(command, "id", source.Id);
                    Add(command, "version", source.Version);
                    Add(command, "index", index);
                    Add(command, "text", chunks[index]);
                    Add(command, "searchable", searchable[index]);
                }, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(string kind, Guid id, long version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || id == Guid.Empty || version < 0)
            throw new ArgumentException("Search tombstone must have a kind, nonempty ID, and nonnegative version.");

        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var accepted = await ExecuteReturningIdAsync("""
            INSERT INTO search_sources (kind, id, version, is_deleted, updated_at_utc)
            VALUES (@kind, @id, @version, true, now())
            ON CONFLICT (kind, id) DO UPDATE SET
                version = EXCLUDED.version,
                is_deleted = true,
                title = '', body = '', path = NULL, url = NULL, updated_at_utc = now(),
                chat_conversation_id = NULL, chat_turn_id = NULL, chat_mode = NULL,
                chat_entity_type = NULL, chat_entity_id = NULL, chat_entity_version = NULL
            WHERE search_sources.version < EXCLUDED.version
               OR (search_sources.version = EXCLUDED.version AND search_sources.is_deleted = false)
            RETURNING id;
            """, command =>
        {
            Add(command, "kind", kind);
            Add(command, "id", id);
            Add(command, "version", version);
        }, cancellationToken);

        if (accepted)
            await ExecuteAsync("DELETE FROM search_chunks WHERE kind = @kind AND source_id = @id;", command =>
            {
                Add(command, "kind", kind);
                Add(command, "id", id);
            }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> ExecuteReturningIdAsync(string sql, Action<DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, configure);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task ExecuteAsync(string sql, Action<DbCommand> configure, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, configure);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(string sql, Action<DbCommand> configure)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        configure(command);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
