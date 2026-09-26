using System.Text.Json;

namespace TaskBoard.Infrastructure;

public interface ITaskSyncOutbox
{
    Task EnqueueAsync(string type, Guid id, string operation, JsonElement? payload, CancellationToken cancellationToken);
    Task EnqueueTaskAsync(string sectionName, string bucket, Guid id, JsonElement payload, CancellationToken cancellationToken);
}
