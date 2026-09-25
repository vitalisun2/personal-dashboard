using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Chat.Infrastructure.Persistence;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Chat.Infrastructure;

internal sealed class ChatSearchIndex(PlatformDbContext dbContext, ISearchIndexer indexer) : ISearchSourceFeed
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // История чата не индексируется в поиск (до прояснения сценариев использования).
    public Task PublishCompletedTurnAsync(ChatTurn turn, string? conversationTitle, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var query = from turn in dbContext.Set<ChatTurnRow>().AsNoTracking()
                    join conversation in dbContext.Set<ChatConversationRow>().AsNoTracking() on turn.ConversationId equals conversation.Id
                    select new { Turn = turn, Conversation = conversation };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var id = DecodeCursor(cursor);
            query = query.Where(x => x.Turn.Id.CompareTo(id) > 0);
        }
        var rows = await query.OrderBy(x => x.Turn.Id)
            .Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        // Чатовые источники исключены из поискового индекса.
        var changes = new SearchSourceChange[0];
        var next = hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].Turn.Id) : null;
        return new SearchSourcePage(changes, next, !hasMore);
    }

    private static SearchIndexSource ToSource(ChatTurn turn, string? title)
    {
        var safeTitle = string.IsNullOrWhiteSpace(title) ? ShortTitle(turn.UserText) : title!;
        var scope = turn.Scope;
        var context = new SearchChatContext(turn.ConversationId, turn.Id, scope.Mode, scope.EntityType, scope.EntityId, scope.EntityVersion);
        return new SearchIndexSource("chat.turn", turn.Id, 1, safeTitle,
            $"User: {turn.UserText}\n\nAssistant: {turn.AssistantText}", null,
            $"/chat?conversationId={turn.ConversationId:D}&turnId={turn.Id:D}", turn.CreatedAtUtc, context);
    }

    private static ChatTurn ToContract(ChatTurnRow row) => new(row.Id, row.ConversationId, row.UserText, row.AssistantText,
        new ChatTurnScope(row.ScopeMode, row.EntityType, row.EntityId, row.EntityVersion), row.RequestedModel, row.ActualModel,
        Enum.Parse<ChatModelRoute>(row.ModelRoute), JsonSerializer.Deserialize<IReadOnlyList<SearchSourceReference>>(row.SourcesJson, JsonOptions) ?? [],
        row.CreatedAtUtc, row.FallbackReason);

    private static string ShortTitle(string text) => text.Length <= 72 ? text : text[..69] + "...";
    private static string EncodeCursor(Guid id) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id.ToString("D")));

    private static Guid DecodeCursor(string cursor)
    {
        try { return Guid.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
        catch (Exception error) when (error is FormatException or ArgumentException) { throw new ArgumentException("Invalid chat source cursor.", nameof(cursor), error); }
    }
}
