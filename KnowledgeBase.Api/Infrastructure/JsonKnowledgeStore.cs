using System.Text.Json;
using KnowledgeBase.Api.Domain;
using KnowledgeBase.Api.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace KnowledgeBase.Api.Infrastructure;

public sealed class JsonKnowledgeStore(IConfiguration configuration, IWebHostEnvironment environment) : IKnowledgeStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string? resolvedPath;
    private string Path => resolvedPath ??= ResolvePath();

    public async Task<KnowledgeDocument> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct); try {
            if (!File.Exists(Path)) return new();
            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<KnowledgeDocument>(stream, json, ct) ?? new();
        } finally { gate.Release(); }
    }

    public async Task WriteAsync(KnowledgeDocument document, CancellationToken ct)
    {
        await gate.WaitAsync(ct); try {
            document.UpdatedAt = DateTimeOffset.UtcNow; var directory = System.IO.Path.GetDirectoryName(Path) ?? "."; Directory.CreateDirectory(directory);
            var temp = Path + ".tmp"; await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, document, json, ct);
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
            File.Move(temp, Path, true);
            var archive = System.IO.Path.Combine(directory, "archive", DateTime.UtcNow.ToString("yyyy-MM-dd")); Directory.CreateDirectory(archive);
            File.Copy(Path, System.IO.Path.Combine(archive, "knowledge.json"), true);
            foreach (var old in Directory.GetDirectories(System.IO.Path.Combine(directory, "archive")).Where(d => DateTime.TryParse(System.IO.Path.GetFileName(d), out var day) && day < DateTime.UtcNow.Date.AddDays(-30))) Directory.Delete(old, true);
        } finally { gate.Release(); }
    }

    private string ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("KNOWLEDGE_FILE") ?? configuration["KNOWLEDGE_FILE"];
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        var tasks = Environment.GetEnvironmentVariable("TASKS_FILE") ?? configuration["TASKS_FILE"];
        if (!string.IsNullOrWhiteSpace(tasks)) return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tasks) ?? ".", "knowledge.json");
        return System.IO.Path.Combine(environment.ContentRootPath, "knowledge.json");
    }
}
