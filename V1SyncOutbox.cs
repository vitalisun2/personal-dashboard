using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeBase.Api.Application;
using TaskBoard.Infrastructure;

public sealed class V1SyncOutbox(IConfiguration configuration, IHttpClientFactory clients, ILogger<V1SyncOutbox> logger)
    : BackgroundService, ITaskSyncOutbox, IKnowledgeSyncOutbox
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly string? _baseUrl = configuration["V2_PEER_URL"];
    private readonly string? _key = configuration["V1_V2_SYNC_KEY"];
    private readonly string _path = ResolvePath(configuration);

    public async Task EnqueueAsync(string type, Guid id, string operation, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        if (id == Guid.Empty || operation is not ("upsert" or "delete")) throw new ArgumentException("Invalid V1 sync operation.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadAsync(cancellationToken);
            state.Operations.Add(new PendingOperation(Guid.NewGuid(), type, id, null, operation, payload));
            await WriteAsync(state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task EnqueueTaskAsync(string sectionName, string bucket, Guid id, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(sectionName) || bucket is not ("backlog" or "today"))
            throw new ArgumentException("A V1 task requires a section name and a compatible bucket.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadAsync(cancellationToken);
            var sectionKey = SectionKey(sectionName, bucket);
            if (!state.SectionIds.TryGetValue(sectionKey, out var sectionId))
            {
                sectionId = StableGuid("personal-dashboard-v1-section:" + sectionKey);
                state.SectionIds[sectionKey] = sectionId;
                var sectionPayload = JsonSerializer.SerializeToElement(new
                {
                    operation = "create", kind = "section", id = sectionId, title = sectionName.Trim(), bucket
                });
                state.Operations.Add(new(StableGuid("personal-dashboard-v1-section-create:" + sectionKey), "tasks.section", sectionId, null, "upsert", sectionPayload));
            }

            var taskPayload = JsonNode.Parse(payload.GetRawText())!.AsObject();
            taskPayload["sectionId"] = sectionId;
            taskPayload["sectionName"] = sectionName.Trim();
            state.Operations.Add(new(Guid.NewGuid(), "tasks.task", id, null, "upsert", JsonSerializer.SerializeToElement(taskPayload)));
            await WriteAsync(state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_key)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await PushNextAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("V1 to V2 sync delivery failed ({ErrorType}); queued changes will retry.", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    internal async Task<bool> PushNextAsync(CancellationToken ct)
    {
        PendingOperation? operation;
        var needsRemoteVersion = false;
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct);
            operation = state.Operations.FirstOrDefault();
            if (operation is null) return false;
            if (operation.ExpectedVersion is null && !IsCreate(operation))
            {
                var known = state.Versions.TryGetValue(Key(operation.Type, operation.Id), out var version) ? version : (long?)null;
                if (known is not null) operation = operation with { ExpectedVersion = known };
                else needsRemoteVersion = true;
                if (!needsRemoteVersion)
                {
                    state.Operations[0] = operation;
                    await WriteAsync(state, ct);
                }
            }
        }
        finally { _gate.Release(); }

        if (needsRemoteVersion)
        {
            var remote = await ReadRemoteVersionAsync(operation!, ct);
            await _gate.WaitAsync(ct);
            try
            {
                var state = await ReadAsync(ct);
                if (state.Operations.FirstOrDefault()?.OperationId != operation!.OperationId) return true;
                if (remote is null)
                {
                    if (operation.Kind == "delete")
                    {
                        state.Operations.RemoveAt(0);
                        await WriteAsync(state, ct);
                        return true;
                    }
                    operation = operation with { Payload = AsCreate(operation.Payload) };
                }
                else operation = operation with { ExpectedVersion = remote };
                state.Operations[0] = operation;
                await WriteAsync(state, ct);
            }
            finally { _gate.Release(); }
        }

        if (operation!.Type == "tasks.task" && operation.Kind == "upsert"
            && await EnsureCurrentTaskSectionAsync(operation, ct)) return true;

        if (operation!.Type == "tasks.section" && IsCreate(operation))
        {
            var section = await FindRemoteSectionAsync(operation, ct);
            if (section is not null)
            {
                await ApplyResolvedSectionAsync(operation, section, ct);
                return true;
            }
        }

        var client = CreateClient();
        var epoch = await client.GetFromJsonAsync<SyncState>("/api/v2/sync/state", _json, ct)
            ?? throw new InvalidDataException("V2 sync state response was empty.");
        var body = new
        {
            epoch = epoch.Epoch,
            operations = new[] { new { operationId = operation.OperationId, type = operation.Type, id = operation.Id,
                expectedVersion = operation.ExpectedVersion, kind = operation.Kind, payload = operation.Payload } }
        };
        using var response = await client.PostAsJsonAsync("/api/v2/sync/push", body, _json, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PushResponse>(_json, ct)
            ?? throw new InvalidDataException("V2 sync push response was empty.");
        var applied = result.Results.FirstOrDefault(item => item.OperationId == operation.OperationId)
            ?? throw new InvalidDataException("V2 omitted the queued sync operation result.");
        if (!applied.Applied)
        {
            logger.LogWarning("V1 to V2 sync operation {OperationId} needs conflict resolution: {Reason}", operation.OperationId, applied.ConflictReason);
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct);
            var index = state.Operations.FindIndex(item => item.OperationId == operation.OperationId);
            if (index >= 0)
            {
                state.Operations.RemoveAt(index);
                if (applied.Current is { } current)
                    state.Versions[Key(operation.Type, operation.Id)] = current.Version;
                await WriteAsync(state, ct);
            }
        }
        finally { _gate.Release(); }
        return true;
    }

    private async Task<bool> EnsureCurrentTaskSectionAsync(PendingOperation operation, CancellationToken ct)
    {
        if (operation.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("sectionName", out var nameElement)
            || !payload.TryGetProperty("placement", out var bucketElement)) return false;
        var name = nameElement.GetString() ?? "";
        var bucket = bucketElement.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(name) || bucket is not ("backlog" or "today")) return false;

        var client = CreateClient();
        using var response = await client.GetAsync($"/api/v2/tasks/sections?location={Uri.EscapeDataString(bucket)}", ct);
        response.EnsureSuccessStatusCode();
        var sections = await response.Content.ReadFromJsonAsync<List<RemoteSection>>(_json, ct) ?? [];
        var match = sections.FirstOrDefault(section => NormalizeSection(section.Name) == NormalizeSection(name)
            && string.Equals(section.Location, bucket, StringComparison.OrdinalIgnoreCase));

        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct);
            var index = state.Operations.FindIndex(item => item.OperationId == operation.OperationId);
            if (index < 0) return true;
            var sectionKey = SectionKey(name, bucket);
            if (match is not null)
            {
                state.SectionIds[sectionKey] = match.Id;
                state.Versions[Key("tasks.section", match.Id)] = match.Version;
                var currentId = payload.TryGetProperty("sectionId", out var idElement) && idElement.TryGetGuid(out var parsed)
                    ? parsed : Guid.Empty;
                if (currentId != match.Id)
                {
                    var rewritten = JsonNode.Parse(payload.GetRawText())!.AsObject();
                    rewritten["sectionId"] = match.Id;
                    state.Operations[index] = operation with { Payload = JsonSerializer.SerializeToElement(rewritten) };
                    await WriteAsync(state, ct);
                    return true;
                }
                await WriteAsync(state, ct);
                return false;
            }

            var deterministicId = StableGuid("personal-dashboard-v1-section:" + sectionKey);
            state.SectionIds[sectionKey] = deterministicId;
            var sectionOperationId = StableGuid("personal-dashboard-v1-section-recreate:" + operation.OperationId.ToString("D"));
            if (!state.Operations.Any(item => item.OperationId == sectionOperationId))
            {
                var sectionPayload = JsonSerializer.SerializeToElement(new
                {
                    operation = "create", kind = "section", id = deterministicId, title = name.Trim(), bucket
                });
                state.Operations.Insert(index, new(sectionOperationId, "tasks.section", deterministicId, null, "upsert", sectionPayload));
            }
            if (!payload.TryGetProperty("sectionId", out var queuedId) || !queuedId.TryGetGuid(out var queuedGuid) || queuedGuid != deterministicId)
            {
                var rewritten = JsonNode.Parse(payload.GetRawText())!.AsObject();
                rewritten["sectionId"] = deterministicId;
                var taskIndex = state.Operations.FindIndex(item => item.OperationId == operation.OperationId);
                if (taskIndex >= 0) state.Operations[taskIndex] = operation with { Payload = JsonSerializer.SerializeToElement(rewritten) };
            }
            await WriteAsync(state, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<long?> ReadRemoteVersionAsync(PendingOperation operation, CancellationToken ct)
    {
        var client = CreateClient();
        var route = operation.Type switch
        {
            "tasks.task" => $"/api/v2/tasks/{operation.Id}",
            "knowledge.node" => "/api/v2/knowledge/tree",
            _ => throw new InvalidDataException($"Unsupported V1 sync entity type '{operation.Type}'.")
        };
        using var response = await client.GetAsync(route, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (operation.Type == "tasks.task") return ReadVersion(document.RootElement);
        return FindKnowledgeVersion(document.RootElement, operation.Id);
    }

    private async Task<RemoteSection?> FindRemoteSectionAsync(PendingOperation operation, CancellationToken ct)
    {
        var payload = operation.Payload is { ValueKind: JsonValueKind.Object } value ? value : throw new InvalidDataException("Section create has no payload.");
        var name = payload.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
        var bucket = payload.TryGetProperty("bucket", out var location) ? location.GetString() ?? "" : "";
        var client = CreateClient();
        using var response = await client.GetAsync($"/api/v2/tasks/sections?location={Uri.EscapeDataString(bucket)}", ct);
        response.EnsureSuccessStatusCode();
        var sections = await response.Content.ReadFromJsonAsync<List<RemoteSection>>(_json, ct) ?? [];
        return sections.FirstOrDefault(section => NormalizeSection(section.Name) == NormalizeSection(name)
            && string.Equals(section.Location, bucket, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ApplyResolvedSectionAsync(PendingOperation operation, RemoteSection section, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct);
            var index = state.Operations.FindIndex(item => item.OperationId == operation.OperationId);
            if (index < 0) return;
            var key = SectionKey(section.Name, section.Location);
            state.SectionIds[key] = section.Id;
            state.Versions[Key("tasks.section", section.Id)] = section.Version;
            state.Operations.RemoveAt(index);
            for (var i = 0; i < state.Operations.Count; i++)
            {
                var pending = state.Operations[i];
                if (pending.Type != "tasks.task" || pending.Payload is not { ValueKind: JsonValueKind.Object } payload
                    || !payload.TryGetProperty("sectionId", out var sectionId) || sectionId.GetGuid() != operation.Id) continue;
                var rewritten = JsonNode.Parse(payload.GetRawText())!.AsObject();
                rewritten["sectionId"] = section.Id;
                state.Operations[i] = pending with { Payload = JsonSerializer.SerializeToElement(rewritten) };
            }
            await WriteAsync(state, ct);
        }
        finally { _gate.Release(); }
    }

    private static long? FindKnowledgeVersion(JsonElement node, Guid id)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                if (FindKnowledgeVersion(item, id) is { } version) return version;
            return null;
        }
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("id", out var idElement) && idElement.TryGetGuid(out var currentId) && currentId == id)
            return ReadVersion(node);
        return node.TryGetProperty("children", out var children) ? FindKnowledgeVersion(children, id) : null;
    }

    private static long? ReadVersion(JsonElement value) =>
        value.TryGetProperty("version", out var version) && version.TryGetInt64(out var result) ? result : null;

    private static JsonElement? AsCreate(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value) return payload;
        var objectNode = JsonNode.Parse(value.GetRawText())!.AsObject();
        objectNode["operation"] = "create";
        objectNode["expectedVersion"] = null;
        return JsonSerializer.SerializeToElement(objectNode);
    }

    private static bool IsCreate(PendingOperation operation) => operation.Kind == "upsert"
        && operation.Payload is { ValueKind: JsonValueKind.Object } payload
        && payload.TryGetProperty("operation", out var value)
        && string.Equals(value.GetString(), "create", StringComparison.OrdinalIgnoreCase);

    private static string SectionKey(string name, string bucket) => bucket + ":" + NormalizeSection(name);
    private static string NormalizeSection(string name) => name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[7] = (byte)((guid[7] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }

    private bool Enabled => !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_key);

    private HttpClient CreateClient()
    {
        var client = clients.CreateClient("V2Peer");
        client.BaseAddress = new Uri(_baseUrl!, UriKind.Absolute);
        client.DefaultRequestHeaders.Remove("X-PersonalDashboard-Sync-Key");
        client.DefaultRequestHeaders.Add("X-PersonalDashboard-Sync-Key", _key!);
        return client;
    }

    private async Task<OutboxState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<OutboxState>(stream, _json, ct) ?? new();
    }

    private async Task WriteAsync(OutboxState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, state, _json, ct);
        File.Move(temporary, _path, true);
    }

    private static string ResolvePath(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration["SYNC_OUTBOX_FILE"])) return configuration["SYNC_OUTBOX_FILE"]!;
        var tasksFile = configuration["TASKS_FILE"];
        var directory = string.IsNullOrWhiteSpace(tasksFile) ? AppContext.BaseDirectory : Path.GetDirectoryName(tasksFile)!;
        return Path.Combine(directory, "v1-v2.sync-outbox.json");
    }

    private static string Key(string type, Guid id) => $"{type}:{id:D}";

    private sealed class OutboxState
    {
        public OutboxState() { }
        public List<PendingOperation> Operations { get; set; } = [];
        public Dictionary<string, long> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> SectionIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PendingOperation(Guid OperationId, string Type, Guid Id, long? ExpectedVersion, string Kind, JsonElement? Payload);
    private sealed record SyncState(string Epoch);
    private sealed record PushResponse(IReadOnlyList<PushResult> Results);
    private sealed record PushResult(Guid OperationId, bool Applied, bool Skipped, EntitySnapshot? Current, string? ConflictReason);
    private sealed record EntitySnapshot(string Type, Guid Id, long Version, bool Deleted, JsonElement? Payload);
    private sealed record RemoteSection(Guid Id, string Name, string Location, long Version);
}
