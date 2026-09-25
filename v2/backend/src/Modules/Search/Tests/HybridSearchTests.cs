using PersonalDashboard.V2.Search.Domain;
using Xunit;

namespace PersonalDashboard.V2.Search.Tests;

public sealed class HybridSearchTests
{
    [Fact]
    public void ExhaustiveDecorQueryReturnsBothSourcesAndCollapsesDuplicateChunks()
    {
        var first = Source("Первый декор");
        var second = Source("Второй декор");
        var candidates = new[]
        {
            new SearchCandidate(first, 0, "Декор города: фонари и растения", SemanticScore: .72),
            new SearchCandidate(first, 1, "Ещё про декор города и лавочки", SemanticScore: .69),
            new SearchCandidate(second, 0, "Оформление улиц, декор города, вывески", FullTextScore: .81)
        };

        var ranked = HybridSearch.Rank(new SearchCriteria("декор города", Exhaustive: true), candidates);

        Assert.Equal(2, ranked.Count);
        Assert.Contains(ranked, hit => hit.Id == first.Id);
        Assert.Contains(ranked, hit => hit.Id == second.Id);
        Assert.Equal(2, ranked.Select(hit => hit.Id).Distinct().Count());
    }

    [Fact]
    public void SourceIsLexicalWhenAnyChunkMatchesAndSemanticOnlyWhenNoneDo()
    {
        var lexicalByFts = Source("Первая запись");
        var lexicalBySubstring = Source("Вторая запись");
        var semanticOnly = Source("Третья запись");
        var ranked = HybridSearch.Rank(new SearchCriteria("фонари", Exhaustive: true),
        [
            new SearchCandidate(lexicalByFts, 0, "Другой текст", SemanticScore: .99),
            new SearchCandidate(lexicalByFts, 1, "Содержание", FullTextScore: .2),
            new SearchCandidate(lexicalBySubstring, 0, "Здесь есть фонари", SemanticScore: .8),
            new SearchCandidate(semanticOnly, 0, "Текст про улицу", SemanticScore: .7)
        ]);

        Assert.False(ranked.Single(hit => hit.Id == lexicalByFts.Id).IsSemantic);
        Assert.False(ranked.Single(hit => hit.Id == lexicalBySubstring.Id).IsSemantic);
        Assert.True(ranked.Single(hit => hit.Id == semanticOnly.Id).IsSemantic);
    }

    private static IndexedSource Source(string title)
    {
        var id = Guid.NewGuid();
        return new IndexedSource("knowledge.document", id, 1, title,
            $"/knowledge/documents/{id}", $"/knowledge/documents/{id}", DateTimeOffset.UtcNow);
    }
}
