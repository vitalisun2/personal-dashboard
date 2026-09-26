namespace PersonalDashboard.V2.Agent.Application;

public sealed class ChatModelUnavailableException(string message, Exception innerException) : Exception(message, innerException) { }

public sealed class ChatModelRouter(IEnumerable<IChatModelProvider> providers) : IChatModelRouter
{
    private readonly IReadOnlyDictionary<string, IChatModelProvider> _providers = providers.ToDictionary(x => x.Model, StringComparer.OrdinalIgnoreCase);

    public async Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedModel, "Gemma", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only the configured Gemma model is available.");
        var primary = GetProvider("Gemma");
        try
        {
            var response = await primary.CompleteAsync(request, cancellationToken);
            if (!IsUsable(response, request)) throw new InvalidOperationException("Gemma returned neither text nor tool calls.");
            return new RoutedCompletion(response, "Gemma", "Gemma", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            throw new ChatModelUnavailableException("Gemma is unavailable or returned an unusable response.", error);
        }
    }

    private IChatModelProvider GetProvider(string model) =>
        _providers.TryGetValue(model, out var provider) ? provider : throw new InvalidOperationException($"No provider is configured for {model}.");

    private static bool IsUsable(ModelCompletion completion, ModelCompletionRequest request)
    {
        if (completion.ToolCalls.Count > 0)
        {
            var knownTools = request.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var call in completion.ToolCalls)
            {
                if (!knownTools.Contains(call.Name)) throw new InvalidDataException($"Unknown tool call '{call.Name}'.");
                using var arguments = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
                if (arguments.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException($"Tool call '{call.Name}' arguments must be an object.");
            }
            return true;
        }
        return !string.IsNullOrWhiteSpace(completion.Content);
    }

}
