using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using TaskBoard.Application;
using TaskBoard.Domain;

namespace TaskBoard.Infrastructure;

/// <summary>JSON persistence owned by the task module. It deliberately keeps the established tasks.json contract.</summary>
public sealed class TaskStore : ITaskRepository
{
    private const int ArchiveRetentionDays = 30;
    private readonly string _path;
    private readonly bool _protectionEnabled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public TaskStore(IWebHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("TASKS_FILE");
        _path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(environment.ContentRootPath, "tasks.json") : configured;
        _protectionEnabled = string.Equals(Environment.GetEnvironmentVariable("TASKS_PROTECTION_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
    }

    public TaskStore(string path) => _path = path;
    internal TaskStore(string path, bool protectionEnabled) { _path = path; _protectionEnabled = protectionEnabled; }

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { return await ReadUnsafeAsync(cancellationToken); } finally { _gate.Release(); } }

    public async Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { return (await ReadUnsafeAsync(cancellationToken)).FirstOrDefault(x => x.Id == id); } finally { _gate.Release(); } }

    public async Task AddAsync(TaskItem item, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { var items = (await ReadUnsafeAsync(cancellationToken)).ToList(); items.Insert(0, item); await WriteUnsafeAsync(items, cancellationToken); } finally { _gate.Release(); } }

    public async Task<TaskItem?> UpdateAsync(Guid id, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { var items = (await ReadUnsafeAsync(cancellationToken)).ToList(); var index = items.FindIndex(x => x.Id == id); if (index < 0) return null; items[index] = update(items[index]); await WriteUnsafeAsync(items, cancellationToken); return items[index]; } finally { _gate.Release(); } }

    public async Task<TaskItem?> UpdateIfAsync(Guid id, Func<TaskItem, bool> condition, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadUnsafeAsync(cancellationToken)).ToList();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0 || !condition(items[index])) return null;
            items[index] = update(items[index]);
            await WriteUnsafeAsync(items, cancellationToken);
            return items[index];
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { await WriteUnsafeAsync((await ReadUnsafeAsync(cancellationToken)).Where(x => x.Id != id).ToList(), cancellationToken); } finally { _gate.Release(); } }

    public async Task<int> RenameSectionAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        oldName = oldName.Trim(); newName = newName.Trim();
        if (oldName.Length == 0 || newName.Length == 0) return 0;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken); var changed = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].Section.Equals(oldName, StringComparison.OrdinalIgnoreCase) || items[i].Section.Equals(newName, StringComparison.Ordinal)) continue;
                items[i] = items[i] with { Section = newName }; changed++;
            }
            if (changed > 0) await WriteUnsafeAsync(items, cancellationToken);
            return changed;
        }
        finally { _gate.Release(); }
    }

    public async Task<int?> RenameSectionIfMembersAsync(string oldName, string newName, IReadOnlyCollection<Guid> expectedTaskIds, CancellationToken cancellationToken = default)
    {
        oldName = oldName.Trim(); newName = newName.Trim();
        if (oldName.Length == 0 || newName.Length == 0) return 0;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var members = items.Where(item => item.Section.Equals(oldName, StringComparison.OrdinalIgnoreCase)).Select(item => item.Id).ToHashSet();
            if (!members.SetEquals(expectedTaskIds)) return null;
            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase) && items.Any(item => item.Section.Equals(newName, StringComparison.OrdinalIgnoreCase))) return null;
            var changed = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].Section.Equals(oldName, StringComparison.OrdinalIgnoreCase) || items[i].Section.Equals(newName, StringComparison.Ordinal)) continue;
                items[i] = items[i] with { Section = newName }; changed++;
            }
            if (changed > 0) await WriteUnsafeAsync(items, cancellationToken);
            return changed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReorderTasksAsync(TaskBucket bucket, string section, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        section = section.Trim();
        if (section.Length == 0 || orderedIds.Distinct().Count() != orderedIds.Count) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var current = items.Where(x => x.Bucket == bucket && x.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (current.Length != orderedIds.Count || !current.Select(x => x.Id).ToHashSet().SetEquals(orderedIds)) return false;
            var byId = current.ToDictionary(x => x.Id); var next = 0;
            for (var i = 0; i < items.Count; i++) if (items[i].Bucket == bucket && items[i].Section.Equals(section, StringComparison.OrdinalIgnoreCase)) items[i] = byId[orderedIds[next++]];
            await WriteUnsafeAsync(items, cancellationToken); return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReorderSectionsAsync(TaskBucket bucket, IReadOnlyList<string> orderedSections, CancellationToken cancellationToken = default)
    {
        var requested = orderedSections.Select(x => x?.Trim() ?? string.Empty).ToArray();
        if (requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var current = items.Where(x => x.Bucket == bucket).Select(x => x.Section).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (current.Length != requested.Length || !current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(requested)) return false;
            var ordered = requested.SelectMany(section => items.Where(x => x.Bucket == bucket && x.Section.Equals(section, StringComparison.OrdinalIgnoreCase))).ToArray(); var next = 0;
            for (var i = 0; i < items.Count; i++) if (items[i].Bucket == bucket) items[i] = ordered[next++];
            await WriteUnsafeAsync(items, cancellationToken); return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<TaskItem>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            if (!_protectionEnabled) return [];
            var backup = BackupPath();
            if (!File.Exists(backup)) throw new InvalidOperationException($"Task data file is missing: {_path}. Restore it from archive or {backup}.");
            File.Copy(backup, _path, overwrite: false);
        }
        await using var stream = File.OpenRead(_path);
        var items = await JsonSerializer.DeserializeAsync<List<TaskItem>>(stream, _json, ct) ?? [];
        return items.Select(item => item with { Section = string.IsNullOrWhiteSpace(item.Section) ? "Общее" : item.Section }).ToList();
    }

    private async Task WriteUnsafeAsync(List<TaskItem> items, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, items, _json, ct);
        if (_protectionEnabled && File.Exists(_path)) File.Replace(temp, _path, BackupPath(), ignoreMetadataErrors: true); else File.Move(temp, _path, true);
        if (_protectionEnabled) await ArchiveCurrentAsync(ct);
    }

    private string BackupPath() => _path + ".bak";
    private async Task ArchiveCurrentAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException($"Task data path has no directory: {_path}");
        var archive = Path.Combine(directory, "archive"); var snapshotDirectory = Path.Combine(archive, DateTime.Now.ToString("yyyy-MM-dd")); Directory.CreateDirectory(snapshotDirectory);
        await using (var source = File.OpenRead(_path)) await using (var target = File.Create(Path.Combine(snapshotDirectory, Path.GetFileName(_path)))) await source.CopyToAsync(target, ct);
        foreach (var stale in Directory.EnumerateDirectories(archive).Select(path => new { Path = path, Name = Path.GetFileName(path) }).Where(x => DateOnly.TryParseExact(x.Name, "yyyy-MM-dd", out _)).OrderByDescending(x => x.Name, StringComparer.Ordinal).Skip(ArchiveRetentionDays)) Directory.Delete(stale.Path, recursive: true);
    }
}
