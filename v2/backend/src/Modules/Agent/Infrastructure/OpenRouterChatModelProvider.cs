using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Agent.Application;

namespace PersonalDashboard.V2.Agent.Infrastructure;

public sealed class OpenRouterChatModelProvider(IHttpClientFactory clients, IConfiguration configuration) : IChatModelProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Model => "DeepSeek";

    public async Task<ModelCompletion> CompleteAsync(ModelCompletionRequest request, CancellationToken cancellationToken)
    {
        var secretPath = configuration["OPENROUTER_API_KEY_FILE"];
        if (string.IsNullOrWhiteSpace(secretPath))
            throw new InvalidOperationException("OPENROUTER_API_KEY_FILE is not configured.");
        var apiKey = (await File.ReadAllTextAsync(secretPath, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OpenRouter API key file is empty.");

        var client = clients.CreateClient(nameof(OpenRouterChatModelProvider));
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"{(configuration["OPENROUTER_URL"] ?? "https://openrouter.ai/api/v1").TrimEnd('/')}/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            model = configuration["OPENROUTER_MODEL"] ?? "deepseek/deepseek-v4-flash-0731",
            messages = request.Messages.Select(ToOpenAiMessage),
            tools = request.Tools.Select(ToOpenAiTool).ToArray(),
            temperature = 0.1,
            max_tokens = 700,
            stream = false
        }, options: JsonOptions);

        using var response = await client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!payload.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidDataException("OpenRouter response has no choices.");
        var answer = choices[0].GetProperty("message");
        var content = answer.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.String
            ? contentValue.GetString()
            : null;
        var calls = new List<ModelToolCall>();
        if (answer.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                calls.Add(new ModelToolCall(
                    call.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    function.GetProperty("name").GetString() ?? throw new InvalidDataException("OpenRouter tool call has no name."),
                    function.GetProperty("arguments").GetString() ?? "{}"));
            }
        }
        return new ModelCompletion(content, calls);
    }

    private static object ToOpenAiTool(ModelTool tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = JsonDocument.Parse(tool.JsonSchema).RootElement.Clone()
        }
    };

    private static object ToOpenAiMessage(ModelMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };
        if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.ToolCallId))
            payload["tool_call_id"] = message.ToolCallId;
        if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            payload["tool_calls"] = message.ToolCalls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.ArgumentsJson }
            }).ToArray();
        return payload;
    }
}
