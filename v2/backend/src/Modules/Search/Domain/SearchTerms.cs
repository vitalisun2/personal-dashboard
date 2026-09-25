using System.Text.RegularExpressions;

namespace PersonalDashboard.V2.Search.Domain;

/// <summary>Shared significant token rules for lexical retrieval and ranking.</summary>
public static partial class SearchTerms
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "how", "i", "in", "is", "it", "me", "of", "on", "or", "the", "to", "was", "what", "where", "which", "who", "with", "you",
        "а", "без", "бы", "в", "во", "вот", "все", "всё", "где", "да", "для", "до", "его", "ее", "её", "если", "же", "за", "и", "из", "или", "как", "ко", "когда", "кто", "ли", "мне", "мы", "на", "над", "не", "но", "о", "об", "от", "по", "под", "про", "с", "со", "так", "то", "у", "я"
    };

    public static string[] Significant(string text) =>
        Words().Matches(text).Select(match => match.Value.ToLowerInvariant())
            .Where(word => word.Length > 1 && !StopWords.Contains(word))
            .Distinct(StringComparer.Ordinal).ToArray();

    [GeneratedRegex("[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
