namespace PersonalDashboard.V2.Search.Domain;

/// <summary>Bounds embedding and FTS input size while retaining enough overlap for boundary-spanning phrases.</summary>
public static class SearchChunker
{
    public const int MaximumCharacters = 1800;
    public const int OverlapCharacters = 180;

    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) return [];

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + MaximumCharacters);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOfAny([' ', '\t', '\r', '\n'], end - 1, end - start);
                if (boundary > start + MaximumCharacters / 2) end = boundary;
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            if (end == text.Length) break;
            start = Math.Max(start + 1, end - OverlapCharacters);
        }
        return chunks;
    }
}
