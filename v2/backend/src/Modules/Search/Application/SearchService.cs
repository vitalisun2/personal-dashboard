using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Application;

public sealed class SearchService(ISearchCandidateStore candidateStore) : ISearchService
{
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return new SearchResponse([], null, true, null);

        var pageSize = request.PageSize ?? 20;
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request.PageSize));
        var criteria = new SearchCriteria(request.Query, request.Mode == SearchCoverageMode.Exhaustive,
            request.Kinds, request.Context is null ? null : new SearchContextFilter(
                request.Context.ConversationId, request.Context.Mode, request.Context.EntityType,
                request.Context.EntityId, request.Context.EntityVersion),
            request.UpdatedAfterUtc, request.UpdatedBeforeUtc);

        var candidateSet = await candidateStore.FindCandidatesAsync(criteria, request.Mode, cancellationToken);
        var ranked = HybridSearch.Rank(criteria, candidateSet.Candidates);
        var fingerprint = Fingerprint(request, pageSize);
        var offset = DecodeCursor(request.Cursor, fingerprint);
        if (offset > ranked.Count) throw new ArgumentException("Search cursor is beyond the result set.", nameof(request.Cursor));

        var hits = ranked.Skip(offset).Take(pageSize).Select(ToContract).ToArray();
        var nextOffset = offset + hits.Length;
        var hasMore = nextOffset < ranked.Count;
        var complete = candidateSet.IsComplete && !hasMore;
        var note = request.Mode == SearchCoverageMode.Relevant
            ? "Показаны наиболее релевантные результаты. Для проверки полноты выберите «Найти всё»."
            : "Охват включает все точные и полнотекстовые совпадения, а также смысловые кандидаты выше порога сходства.";
        if (!string.IsNullOrWhiteSpace(candidateSet.CoverageNote))
            note = note is null ? candidateSet.CoverageNote : $"{note} {candidateSet.CoverageNote}";
        if (request.Mode == SearchCoverageMode.Exhaustive && !candidateSet.IsComplete && note is null)
            note = "Поиск ещё не охватил все источники.";

        return new SearchResponse(hits, hasMore ? EncodeCursor(nextOffset, fingerprint) : null, complete, note);
    }

    private static SearchHit ToContract(RankedSource ranked) => new(
        new SearchSourceReference(ranked.Kind, ranked.Id, ranked.Version, ranked.Url, ranked.Title,
            ranked.Path, ranked.Snippet, ranked.UpdatedAtUtc, ranked.IsChatHistory,
            ranked.ChatContext is null ? null : new SearchChatContext(ranked.ChatContext.ConversationId,
                ranked.ChatContext.TurnId, ranked.ChatContext.Mode, ranked.ChatContext.EntityType,
                ranked.ChatContext.EntityId, ranked.ChatContext.EntityVersion)), ranked.Score,
        ranked.IsSemantic ? SearchMatchKind.Semantic : SearchMatchKind.Lexical);

    private static string Fingerprint(SearchRequest request, int pageSize)
    {
        var material = JsonSerializer.Serialize(new
        {
            query = request.Query.Trim(), mode = request.Mode,
            kinds = request.Kinds?.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            context = request.Context, after = request.UpdatedAfterUtc, before = request.UpdatedBeforeUtc, pageSize
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
    }

    private static string EncodeCursor(int offset, string fingerprint)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{fingerprint}:{offset}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int DecodeCursor(string? cursor, string fingerprint)
    {
        if (cursor is null) return 0;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split(':', 2);
            if (parts.Length != 2 || !string.Equals(parts[0], fingerprint, StringComparison.Ordinal)
                || !int.TryParse(parts[1], out var offset) || offset < 0)
                throw new ArgumentException("Search cursor does not match this request.", nameof(cursor));
            return offset;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Invalid search cursor.", nameof(cursor), exception);
        }
    }
}
