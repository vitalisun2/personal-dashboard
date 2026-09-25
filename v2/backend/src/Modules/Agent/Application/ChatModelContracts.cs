namespace PersonalDashboard.V2.Agent.Application;

public sealed record ModelMessage(string Role, string Content, IReadOnlyList<ModelToolCall>? ToolCalls = null, string? ToolCallId = null, string? Name = null);
public sealed record ModelTool(string Name, string Description, string JsonSchema);
public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ModelCompletionRequest(IReadOnlyList<ModelMessage> Messages, IReadOnlyList<ModelTool> Tools);
public sealed record ModelCompletion(string? Content, IReadOnlyList<ModelToolCall> ToolCalls);
public sealed record RoutedCompletion(ModelCompletion Completion, string RequestedModel, string ActualModel, string? FallbackReason);

public interface IChatModelProvider
{
    string Model { get; }
    Task<ModelCompletion> CompleteAsync(ModelCompletionRequest request, CancellationToken cancellationToken);
}

public interface IChatModelRouter
{
    Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken);
}
