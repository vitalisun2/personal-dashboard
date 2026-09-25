using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Search.Application;

namespace PersonalDashboard.V2.Search.Infrastructure;

/// <summary>Repairs missed post-commit index calls from the durable journal plus one authoritative source-feed scan.</summary>
internal sealed class SearchJournalCatchUpWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SearchJournalCatchUpWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NoFeedDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _reconciliationInterval = TimeSpan.FromMinutes(ParseInterval(configuration["V2_SEARCH_RECONCILIATION_INTERVAL_MINUTES"]));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var lastReconciliation = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollDelay;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var db = services.GetRequiredService<PlatformDbContext>();
                var journal = services.GetRequiredService<IEntityChangeJournal>();
                var rebuilder = services.GetRequiredService<SearchIndexRebuilder>();
                var cursor = await ReadCursorAsync(db, stoppingToken);
                var changes = await ReadPendingChangesAsync(journal, cursor, stoppingToken);
                var shouldReconcile = changes.LatestSequence > cursor
                    || DateTimeOffset.UtcNow - lastReconciliation >= _reconciliationInterval;

                if (shouldReconcile)
                {
                    if (!rebuilder.HasFeeds)
                    {
                        logger.LogWarning("Search catch-up has journal changes but no source feeds are registered; cursor remains at {Sequence}.", cursor);
                        delay = NoFeedDelay;
                    }
                    else
                    {
                        await rebuilder.RebuildAsync(stoppingToken);
                        if (changes.LatestSequence > cursor)
                            await AdvanceCursorAsync(db, cursor, changes.LatestSequence, stoppingToken);
                        lastReconciliation = DateTimeOffset.UtcNow;
                        retryDelay = TimeSpan.FromSeconds(2);
                        logger.LogInformation("Search reconciliation completed; indexed source feeds after {ChangeCount} coalesced journal changes through sequence {Sequence}.",
                            changes.CoalescedCount, changes.LatestSequence);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Search journal catch-up failed; its cursor was not advanced.");
                delay = retryDelay;
                retryDelay = TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, retryDelay.TotalSeconds * 2));
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private static async Task<long> ReadCursorAsync(PlatformDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sequence FROM search_catchup_cursor WHERE id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<PendingJournalChanges> ReadPendingChangesAsync(IEntityChangeJournal journal,
        long cursor, CancellationToken cancellationToken)
    {
        var latestSequence = cursor;
        var coalesced = new Dictionary<(string Type, Guid Id), EntityChange>();
        while (true)
        {
            var requestedAfter = latestSequence;
            var page = await journal.ReadAfterAsync(requestedAfter, 250, cancellationToken);
            foreach (var change in page.Changes)
            {
                if (change.Sequence <= requestedAfter)
                    throw new InvalidOperationException("Entity change journal returned a non-advancing sequence.");
                coalesced[(change.Snapshot.Type, change.Snapshot.Id)] = change;
            }

            if (page.NextSequence < latestSequence || page.NextSequence < requestedAfter)
                throw new InvalidOperationException("Entity change journal page returned a backwards sequence.");
            latestSequence = page.NextSequence;
            if (page.IsComplete) break;
            if (page.NextSequence <= requestedAfter)
                throw new InvalidOperationException("Entity change journal page did not advance its cursor.");
        }

        return new PendingJournalChanges(latestSequence, coalesced.Count);
    }

    private static async Task AdvanceCursorAsync(PlatformDbContext db, long previous, long next,
        CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "UPDATE search_catchup_cursor SET sequence = @next WHERE id = 1 AND sequence = @previous;";
        Add(command, "next", next);
        Add(command, "previous", previous);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return;

        await using var read = db.Database.GetDbConnection().CreateCommand();
        read.CommandText = "SELECT sequence FROM search_catchup_cursor WHERE id = 1;";
        var actual = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (actual < next)
            throw new InvalidOperationException($"Search cursor changed unexpectedly from {previous} to {actual}.");
    }

    private static void Add(DbCommand command, string name, long value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static int ParseInterval(string? configured)
        => int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? Math.Clamp(minutes, 1, 120) : 15;

    private sealed record PendingJournalChanges(long LatestSequence, int CoalescedCount);
}
