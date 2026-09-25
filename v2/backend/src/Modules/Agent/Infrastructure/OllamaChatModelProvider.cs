using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Agent.Application;

namespace PersonalDashboard.V2.Agent.Infrastructure;

public sealed class OllamaChatModelProvider(IHttpClientFactory clients, IConfiguration configuration) : IChatModelProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Model => "Gemma";

    public async Task<ModelCompletion> CompleteAsync(ModelCompletionRequest request, CancellationToken cancellationToken)
    {
        var url = (configuration["OLLAMA_URL"] ?? "http://localhost:11434").TrimEnd('/');
        var model = configuration["OLLAMA_CHAT_MODEL"] ?? "gemma4:e4b-it-qat";
        using var response = await clients.CreateClient(nameof(OllamaChatModelProvider)).PostAsJsonAsync(
            $"{url}/api/chat",
            new
            {
                model,
                messages = request.Messages.Select(ToOllamaMessage),
                tools = request.Tools.Select(ToOllamaTool).ToArray(),
                stream = false
            }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!payload.RootElement.TryGetProperty("message", out var message))
            throw new InvalidDataException("Ollama response has no message.");

        var content = message.TryGetProperty("content", out var contentValue) ? contentValue.GetString() : null;
        var calls = new List<ModelToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? throw new InvalidDataException("Ollama tool call has no name.");
                var args = function.GetProperty("arguments");
                calls.Add(new ModelToolCall(Guid.NewGuid().ToString("N"), name,
                    args.ValueKind == JsonValueKind.String ? args.GetString()! : args.GetRawText()));
            }
        }
        return new ModelCompletion(content, calls);
    }

    private static object ToOllamaTool(ModelTool tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = JsonDocument.Parse(tool.JsonSchema).RootElement.Clone()
        }
    };

    private static object ToOllamaMessage(ModelMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };
        if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.Name)) payload["tool_name"] = message.Name;
        if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            payload["tool_calls"] = message.ToolCalls.Select(call => new
            {
                function = new
                {
                    name = call.Name,
                    arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement.Clone()
                }
            }).ToArray();
        return payload;
    }
}
