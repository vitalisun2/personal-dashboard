using System.Text.Json;

namespace KnowledgeBase.Api.Application;

public interface IKnowledgeSyncOutbox
{
    Task EnqueueAsync(string type, Guid id, string operation, JsonElement? payload, CancellationToken cancellationToken);
}
