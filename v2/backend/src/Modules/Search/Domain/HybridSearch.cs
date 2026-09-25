namespace PersonalDashboard.V2.Search.Domain;

/// <summary>Fuses lexical, PostgreSQL FTS, and vector rankings and collapses chunk hits to source hits.</summary>
public static class HybridSearch
{
    private const double ReciprocalRankConstant = 60;

    public static IReadOnlyList<RankedSource> Rank(SearchCriteria request, IEnumerable<SearchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);
        var query = request.Query.Trim();
        if (query.Length == 0) return [];

        var terms = SearchTerms.Significant(query);
        var filtered = candidates.Where(c => PassesFilters(c.Source, request))
            .Select(c => (Candidate: c, Lexical: LexicalScore(query, terms, c)))
            .Where(x => x.Lexical > 0 || x.Candidate.SemanticScore is > 0 || x.Candidate.FullTextScore is > 0)
            .ToArray();

        var lexicalRanks = Ranks(filtered, x => x.Lexical);
        var fullTextRanks = Ranks(filtered, x => x.Candidate.FullTextScore ?? 0);
        var semanticRanks = Ranks(filtered, x => x.Candidate.SemanticScore ?? 0);

        return filtered.GroupBy(x => (x.Candidate.Source.Kind, x.Candidate.Source.Id))
            .Select(group =>
            {
                var best = group.Select(x => (Row: x, Score: Fused(x, lexicalRanks, fullTextRanks, semanticRanks)))
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Row.Candidate.ChunkIndex).First();
                var source = best.Row.Candidate.Source;
                var hasLexicalMatch = group.Any(x => x.Lexical > 0 || x.Candidate.FullTextScore is > 0);
                return new RankedSource(source.Kind, source.Id, source.Version, source.Url,
                    source.Title, source.Path, source.UpdatedAtUtc,
                    source.ChatContext,
                    Excerpt(best.Row.Candidate.Text, query),
                    string.Equals(source.Kind, "chat.turn", StringComparison.OrdinalIgnoreCase),
                    IsSemantic: !hasLexicalMatch, best.Score);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    private static Dictionary<(string Kind, Guid Id, int Chunk), int> Ranks(
        IReadOnlyList<(SearchCandidate Candidate, double Lexical)> rows,
        Func<(SearchCandidate Candidate, double Lexical), double> score) => rows
        .Select((row, index) => (row, value: score(row), index))
        .Where(x => double.IsFinite(x.value) && x.value > 0)
        .OrderByDescending(x => x.value).ThenBy(x => x.index)
        .Select((x, index) => (Key: (x.row.Candidate.Source.Kind, x.row.Candidate.Source.Id, x.row.Candidate.ChunkIndex), Rank: index + 1))
        .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Min(x => x.Rank));

    private static double Fused((SearchCandidate Candidate, double Lexical) row,
        IReadOnlyDictionary<(string Kind, Guid Id, int Chunk), int> lexical,
        IReadOnlyDictionary<(string Kind, Guid Id, int Chunk), int> fullText,
        IReadOnlyDictionary<(string Kind, Guid Id, int Chunk), int> semantic)
    {
        var key = (row.Candidate.Source.Kind, row.Candidate.Source.Id, row.Candidate.ChunkIndex);
        return Contribution(lexical, key) + Contribution(fullText, key) + Contribution(semantic, key);
    }

    private static double Contribution(IReadOnlyDictionary<(string Kind, Guid Id, int Chunk), int> ranks, (string Kind, Guid Id, int Chunk) key)
        => ranks.TryGetValue(key, out var rank) ? 1d / (ReciprocalRankConstant + rank) : 0;

    private static double LexicalScore(string query, IReadOnlyList<string> terms, SearchCandidate candidate)
    {
        var score = (candidate.Source.Title + "\n" + candidate.Text).Contains(query, StringComparison.OrdinalIgnoreCase) ? 8d : 0d;
        foreach (var term in terms)
        {
            if (candidate.Source.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
            if (candidate.Text.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    private static bool PassesFilters(IndexedSource source, SearchCriteria request)
    {
        if (request.Kinds is { Count: > 0 } && !request.Kinds.Contains(source.Kind, StringComparer.OrdinalIgnoreCase)) return false;
        if (request.UpdatedAfterUtc is not null && source.UpdatedAtUtc < request.UpdatedAfterUtc) return false;
        if (request.UpdatedBeforeUtc is not null && source.UpdatedAtUtc > request.UpdatedBeforeUtc) return false;
        var filter = request.Context;
        if (filter is null || !string.Equals(source.Kind, "chat.turn", StringComparison.OrdinalIgnoreCase)) return true;
        var context = source.ChatContext;
        if (context is null) return false;
        return (filter.ConversationId is null || filter.ConversationId == context.ConversationId)
            && (filter.Mode is null || string.Equals(filter.Mode, context.Mode, StringComparison.OrdinalIgnoreCase))
            && (filter.EntityType is null || string.Equals(filter.EntityType, source.EntityType, StringComparison.OrdinalIgnoreCase))
            && (filter.EntityId is null || filter.EntityId == source.EntityId)
            && (filter.EntityVersion is null || filter.EntityVersion == source.EntityVersion);
    }

    private static string Excerpt(string text, string query)
    {
        const int size = 160;
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = 0;
        var start = Math.Max(0, at - size / 3);
        var length = Math.Min(text.Length - start, size);
        return (start > 0 ? "…" : "") + text.Substring(start, length) + (start + length < text.Length ? "…" : "");
    }

}
