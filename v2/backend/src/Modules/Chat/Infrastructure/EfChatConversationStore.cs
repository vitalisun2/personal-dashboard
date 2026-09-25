using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Chat.Infrastructure.Persistence;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Chat.Infrastructure;

internal sealed class EfChatConversationStore(PlatformDbContext dbContext) : IChatConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ChatConversationState?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Set<ChatConversationRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : new ChatConversationState(row.Id, row.Title, row.CreatedAtUtc);
    }

    public async Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Conversation ID is required.", nameof(id));
        var existing = await GetConversationAsync(id, cancellationToken);
        if (existing is not null) return existing;
        var now = DateTimeOffset.UtcNow;
        var row = new ChatConversationRow { Id = id, Title = CleanTitle(title), CreatedAtUtc = now, UpdatedAtUtc = now };
        dbContext.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ChatConversationState(row.Id, row.Title, row.CreatedAtUtc);
    }

    public async Task<ChatConversationPage> ListConversationsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.Set<ChatConversationRow>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(cursor))
        {
            var (updated, id) = DecodeCursor(cursor);
            query = query.Where(x => x.UpdatedAtUtc < updated || (x.UpdatedAtUtc == updated && x.Id.CompareTo(id) < 0));
        }
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id)
            .Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var ids = rows.Select(x => x.Id).ToArray();
        var counts = await dbContext.Set<ChatTurnRow>().AsNoTracking().Where(x => ids.Contains(x.ConversationId))
            .GroupBy(x => x.ConversationId).Select(x => new { Id = x.Key, Count = x.LongCount() }).ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var summaries = rows.Select(x => new ChatConversationSummary(x.Id, x.Title, x.UpdatedAtUtc, counts.GetValueOrDefault(x.Id))).ToArray();
        return new ChatConversationPage(summaries, hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].UpdatedAtUtc, rows[^1].Id) : null);
    }

    public async Task<IReadOnlyList<ChatTurn>> GetRecentTurnsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 0, 100);
        var rows = await dbContext.Set<ChatTurnRow>().AsNoTracking().Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(limit)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows.Select(ToContract).ToArray();
    }

    public async Task<ChatTurnPage> GetTurnsPageAsync(Guid conversationId, string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.Set<ChatTurnRow>().AsNoTracking().Where(x => x.ConversationId == conversationId);
        if (!string.IsNullOrEmpty(cursor))
        {
            var (created, id) = DecodeCursor(cursor);
            query = query.Where(x => x.CreatedAtUtc < created || (x.CreatedAtUtc == created && x.Id.CompareTo(id) < 0));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var next = hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id) : null;
        rows.Reverse();
        return new ChatTurnPage(rows.Select(ToContract).ToArray(), next);
    }

    public async Task AppendTurnAsync(ChatTurn turn, CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.Set<ChatConversationRow>().SingleOrDefaultAsync(x => x.Id == turn.ConversationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Conversation {turn.ConversationId} does not exist.");
        if (await dbContext.Set<ChatTurnRow>().AnyAsync(x => x.Id == turn.Id, cancellationToken)) return;
        dbContext.Add(new ChatTurnRow
        {
            Id = turn.Id,
            ConversationId = turn.ConversationId,
            UserText = turn.UserText,
            AssistantText = turn.AssistantText,
            ScopeMode = turn.Scope.Mode,
            EntityType = turn.Scope.EntityType,
            EntityId = turn.Scope.EntityId,
            EntityVersion = turn.Scope.EntityVersion,
            RequestedModel = turn.RequestedModel,
            ActualModel = turn.ActualModel,
            ModelRoute = turn.ModelRoute.ToString(),
            FallbackReason = turn.FallbackReason,
            SourcesJson = JsonSerializer.Serialize(turn.Sources, JsonOptions),
            CreatedAtUtc = turn.CreatedAtUtc
        });
        conversation.UpdatedAtUtc = turn.CreatedAtUtc;
        if (string.IsNullOrWhiteSpace(conversation.Title)) conversation.Title = ShortTitle(turn.UserText);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ChatProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Set<ChatProposalRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : new ChatProposal(row.Id, row.ConversationId, row.TurnId,
            JsonSerializer.Deserialize<IReadOnlyList<ChatProposedAction>>(row.ActionsJson, JsonOptions) ?? [],
            Enum.Parse<ChatProposalState>(row.State), row.ConfirmationId, row.CreatedAtUtc);
    }

    public async Task<ChatProposal?> GetProposalForTurnAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Set<ChatProposalRow>().AsNoTracking().SingleOrDefaultAsync(x => x.TurnId == turnId, cancellationToken);
        return row is null ? null : new ChatProposal(row.Id, row.ConversationId, row.TurnId,
            JsonSerializer.Deserialize<IReadOnlyList<ChatProposedAction>>(row.ActionsJson, JsonOptions) ?? [],
            Enum.Parse<ChatProposalState>(row.State), row.ConfirmationId, row.CreatedAtUtc);
    }

    public async Task SaveProposalAsync(ChatProposal proposal, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Set<ChatProposalRow>().SingleOrDefaultAsync(x => x.Id == proposal.Id, cancellationToken);
        if (row is null)
        {
            row = new ChatProposalRow { Id = proposal.Id };
            dbContext.Add(row);
        }
        row.ConversationId = proposal.ConversationId;
        row.TurnId = proposal.TurnId;
        row.ActionsJson = JsonSerializer.Serialize(proposal.Actions, JsonOptions);
        row.State = proposal.State.ToString();
        row.ConfirmationId = proposal.ConfirmationId;
        row.CreatedAtUtc = proposal.CreatedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DismissPendingProposalsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var count = await dbContext.Set<ChatProposalRow>().Where(x => x.ConversationId == conversationId && x.State == ChatProposalState.Pending.ToString())
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, ChatProposalState.Dismissed.ToString()), cancellationToken);
        return count;
    }

    public async Task<bool> TryChangeProposalStateAsync(Guid id, ChatProposalState expectedState, ChatProposalState newState,
        Guid? confirmationId, CancellationToken cancellationToken = default)
    {
        var count = await dbContext.Set<ChatProposalRow>().Where(x => x.Id == id && x.State == expectedState.ToString())
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, newState.ToString())
                .SetProperty(x => x.ConfirmationId, confirmationId), cancellationToken);
        return count == 1;
    }

    private static ChatTurn ToContract(ChatTurnRow row) => new(row.Id, row.ConversationId, row.UserText, row.AssistantText,
        new ChatTurnScope(row.ScopeMode, row.EntityType, row.EntityId, row.EntityVersion), row.RequestedModel, row.ActualModel,
        Enum.Parse<ChatModelRoute>(row.ModelRoute), JsonSerializer.Deserialize<IReadOnlyList<SearchSourceReference>>(row.SourcesJson, JsonOptions) ?? [],
        row.CreatedAtUtc, row.FallbackReason);

    private static string? CleanTitle(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(240, title.Trim().Length)];
    private static string ShortTitle(string text) => text.Length <= 60 ? text : text[..57] + "...";
    private static string EncodeCursor(DateTimeOffset timestamp, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{timestamp.UtcTicks}|{id:D}"));

    private static (DateTimeOffset Timestamp, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return (new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero), Guid.Parse(parts[1]));
        }
        catch (Exception error) when (error is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("Invalid chat page cursor.", nameof(cursor), error);
        }
    }
}
