namespace PersonalDashboard.V2.Agent.Application;

public sealed class ChatModelRouter(IEnumerable<IChatModelProvider> providers) : IChatModelRouter
{
    private readonly IReadOnlyDictionary<string, IChatModelProvider> _providers = providers.ToDictionary(x => x.Model, StringComparer.OrdinalIgnoreCase);

    public async Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken)
    {
        var primary = GetProvider(requestedModel);
        try
        {
            var response = await primary.CompleteAsync(request, cancellationToken);
            if (IsUsable(response, request)) return new RoutedCompletion(response, requestedModel, requestedModel, null);
            throw new InvalidOperationException($"{requestedModel} returned neither text nor tool calls.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception primaryError)
        {
            if (string.Equals(requestedModel, "DeepSeek", StringComparison.OrdinalIgnoreCase)) throw;
            var fallbackModel = string.Equals(requestedModel, "Gemma", StringComparison.OrdinalIgnoreCase) ? "DeepSeek" : "Gemma";
            if (!_providers.TryGetValue(fallbackModel, out var fallback)) throw;
            try
            {
                var response = await fallback.CompleteAsync(request, cancellationToken);
                if (!IsUsable(response, request)) throw new InvalidOperationException($"{fallbackModel} returned neither text nor valid tool calls.");
                return new RoutedCompletion(response, requestedModel, fallbackModel, DescribeFailure(primaryError));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallbackError)
            {
                throw new AggregateException("The requested chat model and its fallback both failed.", primaryError, fallbackError);
            }
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

    private static string DescribeFailure(Exception error) => error is HttpRequestException
        ? "Requested provider was unavailable."
        : "Requested provider returned an unusable response.";
}
