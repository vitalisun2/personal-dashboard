using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using TaskBoard.Application;
using TaskBoard.Domain;
using TaskStatus = TaskBoard.Domain.TaskStatus;

namespace TaskBoard.Infrastructure;

public sealed record TaskBatchUpdate(Guid Id, string ExpectedTitle, string ExpectedDescription, string ExpectedSection, string Title, string Description, string Section);
public sealed record TaskBatchResult(bool Applied, string? Error);
public sealed record TaskSectionRename(string OldName, string NewName, Guid[] ExpectedTaskIds);
public sealed record TaskSectionRenameResult(bool Applied, string? Error);
public sealed record TaskVersionToggleResult(bool Found, TaskItem? Item);

/// <summary>JSON persistence owned by the task module. It deliberately keeps the established tasks.json contract.</summary>
public sealed class TaskStore : ITaskRepository
{
    private const int ArchiveRetentionDays = 30;
    private readonly string _path;
    private readonly ITaskSyncOutbox? _syncOutbox;
    private readonly bool _protectionEnabled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public TaskStore(IWebHostEnvironment environment, ITaskSyncOutbox? syncOutbox = null)
    {
        var configured = Environment.GetEnvironmentVariable("TASKS_FILE");
        _path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(environment.ContentRootPath, "tasks.json") : configured;
        _protectionEnabled = string.Equals(Environment.GetEnvironmentVariable("TASKS_PROTECTION_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
        _syncOutbox = syncOutbox;
    }

    public TaskStore(string path, ITaskSyncOutbox? syncOutbox = null)
    { _path = path; _syncOutbox = syncOutbox; }
    internal TaskStore(string path, bool protectionEnabled) { _path = path; _protectionEnabled = protectionEnabled; }

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { return await ReadUnsafeAsync(cancellationToken); } finally { _gate.Release(); } }

    public async Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { return (await ReadUnsafeAsync(cancellationToken)).FirstOrDefault(x => x.Id == id); } finally { _gate.Release(); } }

    public async Task AddAsync(TaskItem item, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { var items = (await ReadUnsafeAsync(cancellationToken)).ToList(); items.Insert(0, item with { PreviousVersion = null, ShowingAlternate = false }); await WriteUnsafeAsync(items, cancellationToken); } finally { _gate.Release(); } }

    public async Task<TaskItem?> UpdateAsync(Guid id, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); try { var items = (await ReadUnsafeAsync(cancellationToken)).ToList(); var index = items.FindIndex(x => x.Id == id); if (index < 0) return null; items[index] = items[index].CaptureContentEdit(update(items[index])); await WriteUnsafeAsync(items, cancellationToken); return items[index]; } finally { _gate.Release(); } }

    public async Task<TaskItem?> UpdateIfAsync(Guid id, Func<TaskItem, bool> condition, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadUnsafeAsync(cancellationToken)).ToList();
            var index = items.FindIndex(x => x.Id == id);
            if (index < 0 || !condition(items[index])) return null;
            items[index] = items[index].CaptureContentEdit(update(items[index]));
            await WriteUnsafeAsync(items, cancellationToken);
            return items[index];
        }
        finally { _gate.Release(); }
    }

    public async Task<TaskVersionToggleResult> ToggleVersionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var index = items.FindIndex(item => item.Id == id);
            if (index < 0) return new(false, null);
            var current = items[index];
            if (current.PreviousVersion is not { } alternate) return new(true, null);
            var swapped = current with
            {
                Title = alternate.Title,
                Description = alternate.Description,
                Section = alternate.Section,
                PreviousVersion = new TaskContentVersion(current.Title, current.Description, current.Section),
                ShowingAlternate = !current.ShowingAlternate
            };
            items[index] = swapped;
            await WriteUnsafeAsync(items, cancellationToken);
            return new(true, swapped);
        }
        finally { _gate.Release(); }
    }

    public async Task<TaskBatchResult> ApplyBatchIfCurrentAsync(IReadOnlyList<TaskBatchUpdate> updates, CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0) return new(false, "Пакет изменений пуст.");
        if (updates.Any(update => update.Id == Guid.Empty) || updates.Select(update => update.Id).Distinct().Count() != updates.Count)
            return new(false, "В пакете есть пустой или повторяющийся идентификатор задачи.");
        if (updates.Any(update => string.IsNullOrWhiteSpace(update.Title) || string.IsNullOrWhiteSpace(update.Section)))
            return new(false, "Название задачи и раздел обязательны.");
        if (updates.Any(update => !string.Equals(update.ExpectedSection, update.Section, StringComparison.Ordinal)))
            return new(false, "Перенос задач в другой раздел через чат не поддерживается.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var byId = items.ToDictionary(item => item.Id);
            foreach (var update in updates)
            {
                if (!byId.TryGetValue(update.Id, out var current)) return new(false, "Одна из задач больше не существует.");
                if (!string.Equals(current.Title, update.ExpectedTitle, StringComparison.Ordinal)
                    || !string.Equals(current.Description, update.ExpectedDescription, StringComparison.Ordinal)
                    || !string.Equals(current.Section, update.ExpectedSection, StringComparison.Ordinal))
                    return new(false, "Данные одной из задач изменились после подготовки правки. Повторите запрос с актуальными данными.");
            }

            foreach (var update in updates)
            {
                var index = items.FindIndex(item => item.Id == update.Id);
                items[index] = items[index].CaptureContentEdit(items[index] with { Title = update.Title.Trim(), Description = update.Description, Section = update.Section });
            }
            await WriteUnsafeAsync(items, cancellationToken);
            return new(true, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<TaskSectionRenameResult> ApplySectionRenamesIfMembersAsync(IReadOnlyList<TaskSectionRename> renames, CancellationToken cancellationToken = default)
    {
        if (renames.Count == 0) return new(false, "Пакет переименования разделов пуст.");
        var normalized = renames.Select(rename => new
        {
            Rename = rename,
            OldName = rename.OldName?.Trim() ?? string.Empty,
            NewName = rename.NewName?.Trim() ?? string.Empty,
            ExpectedIds = rename.ExpectedTaskIds ?? []
        }).ToArray();
        if (normalized.Any(item => item.OldName.Length == 0 || item.NewName.Length == 0))
            return new(false, "Название раздела обязательно.");
        if (normalized.Select(item => item.OldName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length
            || normalized.Select(item => item.NewName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            return new(false, "В пакете есть повторяющиеся разделы.");
        if (normalized.Any(item => item.ExpectedIds.Length == 0 || item.ExpectedIds.Distinct().Count() != item.ExpectedIds.Length))
            return new(false, "Для каждого раздела нужен непустой список уникальных задач.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var renamedOldNames = normalized.Select(item => item.OldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in normalized)
            {
                var actualIds = items.Where(task => task.Section.Equals(item.OldName, StringComparison.OrdinalIgnoreCase)).Select(task => task.Id).ToHashSet();
                if (actualIds.Count == 0 || !actualIds.SetEquals(item.ExpectedIds))
                    return new(false, "Состав одного из разделов изменился после подготовки переименования. Повторите запрос с актуальными данными.");
            }

            var destinationNames = normalized.Select(item => item.NewName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var untouchedSections = items.Select(task => task.Section).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(section => !renamedOldNames.Contains(section)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (destinationNames.Overlaps(untouchedSections))
                return new(false, "Раздел с таким названием уже существует.");

            var originalSections = items.Select(task => task.Section).ToArray();
            foreach (var item in normalized)
            {
                foreach (var index in Enumerable.Range(0, items.Count).Where(index => originalSections[index].Equals(item.OldName, StringComparison.OrdinalIgnoreCase)))
                    items[index] = items[index].CaptureContentEdit(items[index] with { Section = item.NewName });
            }
            await WriteUnsafeAsync(items, cancellationToken);
            return new(true, null);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default, bool publishSync = true)
    { await _gate.WaitAsync(cancellationToken); try { await WriteUnsafeAsync((await ReadUnsafeAsync(cancellationToken)).Where(x => x.Id != id).ToList(), cancellationToken, publishSync); } finally { _gate.Release(); } }

    public async Task ImportAsync(TaskItem item, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var index = items.FindIndex(existing => existing.Id == item.Id);
            if (index < 0) items.Insert(0, item with { PreviousVersion = null, ShowingAlternate = false });
            else
            {
                var current = items[index];
                var imported = item with { CreatedAt = current.CreatedAt, PreviousVersion = current.PreviousVersion, ShowingAlternate = current.ShowingAlternate };
                if ((current with { PreviousVersion = null, ShowingAlternate = false }) == (imported with { PreviousVersion = null, ShowingAlternate = false })) return;
                items[index] = imported;
            }
            await WriteUnsafeAsync(items, cancellationToken, publishSync: false);
        }
        finally { _gate.Release(); }
    }

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
                items[i] = items[i].CaptureContentEdit(items[i] with { Section = newName }); changed++;
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
                items[i] = items[i].CaptureContentEdit(items[i] with { Section = newName }); changed++;
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

    private async Task WriteUnsafeAsync(List<TaskItem> items, CancellationToken ct, bool publishSync = true)
    {
        var previous = publishSync && _syncOutbox is not null ? await ReadUnsafeAsync(ct) : null;
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, items, _json, ct);
        if (_protectionEnabled && File.Exists(_path)) File.Replace(temp, _path, BackupPath(), ignoreMetadataErrors: true); else File.Move(temp, _path, true);
        if (previous is not null) await EnqueueChangesAsync(previous, items, ct);
        if (_protectionEnabled) await ArchiveCurrentAsync(ct);
    }

    private async Task EnqueueChangesAsync(IReadOnlyList<TaskItem> previous, IReadOnlyList<TaskItem> current, CancellationToken ct)
    {
        var before = previous.ToDictionary(item => item.Id);
        var after = current.ToDictionary(item => item.Id);
        foreach (var removed in before.Keys.Except(after.Keys))
            await _syncOutbox!.EnqueueAsync("tasks.task", removed, "delete", null, ct);

        foreach (var item in current)
        {
            if (before.TryGetValue(item.Id, out var old) && CompatibleTaskStateEquals(old, item)) continue;
            var payload = JsonSerializer.SerializeToElement(new
            {
                operation = before.ContainsKey(item.Id) ? "update" : "create",
                kind = "task",
                id = item.Id,
                title = item.Title,
                description = item.Description,
                placement = item.Bucket == TaskBucket.Today ? "today" : "backlog",
                workStatus = item.Status switch { TaskStatus.InProgress => "inProgress", TaskStatus.Completed => "done", _ => "new" }
            });
            var bucket = item.Bucket == TaskBucket.Today ? "today" : "backlog";
            await _syncOutbox!.EnqueueTaskAsync(item.Section, bucket, item.Id, payload, ct);
        }
    }

    private static bool CompatibleTaskStateEquals(TaskItem left, TaskItem right) =>
        left.Title == right.Title && left.Description == right.Description && left.Bucket == right.Bucket &&
        left.Status == right.Status && left.Section == right.Section && left.CreatedAt == right.CreatedAt;

    private string BackupPath() => _path + ".bak";
    private async Task ArchiveCurrentAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException($"Task data path has no directory: {_path}");
        var archive = Path.Combine(directory, "archive"); var snapshotDirectory = Path.Combine(archive, DateTime.Now.ToString("yyyy-MM-dd")); Directory.CreateDirectory(snapshotDirectory);
        await using (var source = File.OpenRead(_path)) await using (var target = File.Create(Path.Combine(snapshotDirectory, Path.GetFileName(_path)))) await source.CopyToAsync(target, ct);
        foreach (var stale in Directory.EnumerateDirectories(archive).Select(path => new { Path = path, Name = Path.GetFileName(path) }).Where(x => DateOnly.TryParseExact(x.Name, "yyyy-MM-dd", out _)).OrderByDescending(x => x.Name, StringComparer.Ordinal).Skip(ArchiveRetentionDays)) Directory.Delete(stale.Path, recursive: true);
    }
}
