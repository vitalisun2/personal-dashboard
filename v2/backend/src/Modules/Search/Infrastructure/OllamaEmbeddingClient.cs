using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Search.Infrastructure;

/// <summary>Small adapter for Ollama's /api/embed endpoint used by both indexing and query search.</summary>
public sealed class OllamaEmbeddingClient(HttpClient httpClient, IConfiguration configuration)
{
    private readonly string _endpoint = (configuration["OLLAMA_URL"] ?? "http://localhost:11434").TrimEnd('/') + "/api/embed";
    private readonly string _model = configuration["OLLAMA_EMBED_MODEL"] ?? "embeddinggemma";
    public string ModelName => _model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> input,
        CancellationToken cancellationToken = default)
    {
        if (input.Count == 0) return [];
        using var response = await httpClient.PostAsJsonAsync(_endpoint,
            new EmbedRequest(_model, input), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
        if (result?.Embeddings is not { Count: > 0 } vectors || vectors.Count != input.Count)
            throw new InvalidOperationException("Ollama returned an unexpected number of embeddings.");

        var dimension = vectors[0].Length;
        if (dimension == 0 || vectors.Any(vector => vector.Length != dimension || vector.Any(value => !float.IsFinite(value))))
            throw new InvalidOperationException("Ollama returned invalid embedding dimensions or values.");
        return vectors;
    }

    public static string ToVectorLiteral(IEnumerable<float> vector)
        => "[" + string.Join(',', vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private sealed record EmbedRequest(string Model, IReadOnlyList<string> Input);
    private sealed record EmbedResponse(IReadOnlyList<float[]> Embeddings);
}
