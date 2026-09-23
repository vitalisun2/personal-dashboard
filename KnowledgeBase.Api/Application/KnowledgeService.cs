using KnowledgeBase.Api.Domain;

namespace KnowledgeBase.Api.Application;

public sealed record KnowledgeNodeDto(Guid Id, string Kind, string Title, Guid? ParentId, int Order, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<KnowledgeNodeDto>? Children = null);
public sealed record KnowledgeDocumentDto(Guid Id, string Kind, string Title, string Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record KnowledgeSnapshotNodeDto(Guid Id, string Kind, string Title, Guid? ParentId, int Order, string Path, string? Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record KnowledgeSnapshotDto(int SchemaVersion, DateTimeOffset UpdatedAt, IReadOnlyList<KnowledgeSnapshotNodeDto> Nodes);

public sealed record CreateSectionRequest(string? Title, Guid? ParentId);
public sealed record CreateDocumentRequest(string? Title, string? Content, Guid? ParentId);
public sealed record RenameRequest(string? Title);
public sealed record UpdateContentRequest(string? Content);
public sealed record MoveRequest(Guid? ParentId, int Order);
public sealed record KnowledgeBatchChange(Guid Id, string Operation, string ExpectedTitle, string? ExpectedContent, Guid? ExpectedParentId, string? NewTitle, string? NewContent);
public sealed record KnowledgeBatchResult(bool Applied, string? Error);
public sealed record KnowledgeCreateSectionDocumentResult(bool Applied, Guid? SectionId, Guid? DocumentId, string? Error);

public interface IKnowledgeStore
{
    Task<KnowledgeDocument> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(KnowledgeDocument document, CancellationToken cancellationToken);
}

public sealed class KnowledgeService(IKnowledgeStore store)
{
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public async Task<KnowledgeBatchResult> ApplyBatchIfCurrentAsync(IReadOnlyList<KnowledgeBatchChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return new(false, "Пакет изменений пуст.");
        if (changes.Any(change => change.Id == Guid.Empty) || changes.Select(change => change.Id).Distinct().Count() != changes.Count)
            return new(false, "В пакете есть пустой или повторяющийся идентификатор узла базы знаний.");
        if (changes.Any(change => change.Operation is not ("rename_document" or "replace_content" or "append_content" or "rename_section")))
            return new(false, "Одна из операций базы знаний не поддерживается.");
        if (changes.Any(change => change.Operation is "rename_document" or "rename_section" && string.IsNullOrWhiteSpace(change.NewTitle)))
            return new(false, "Название узла обязательно.");
        if (changes.Any(change => change.Operation is "replace_content" or "append_content" && change.NewContent is null))
            return new(false, "Для изменения документа требуется текст.");

        await mutationGate.WaitAsync(ct);
        try
        {
            var data = await store.ReadAsync(ct);
            var byId = data.Nodes.ToDictionary(node => node.Id);
            var targets = new Dictionary<Guid, KnowledgeNode>();
            foreach (var change in changes)
            {
                if (!byId.TryGetValue(change.Id, out var node)) return new(false, "Один из узлов базы знаний больше не существует.");
                var expectsSection = change.Operation == "rename_section";
                if (node.IsSection != expectsSection) return new(false, expectsSection ? "Раздел для переименования не найден." : "Документ для изменения не найден.");
                if (!string.Equals(node.Title, change.ExpectedTitle, StringComparison.Ordinal)
                    || node.ParentId != change.ExpectedParentId
                    || (change.Operation != "rename_section" && !string.Equals(node.Content ?? "", change.ExpectedContent ?? "", StringComparison.Ordinal)))
                    return new(false, "Один из узлов базы знаний изменился после подготовки правки. Повторите запрос с актуальными данными.");
                targets.Add(change.Id, node);
            }

            var renameChanges = changes.Where(change => change.Operation is "rename_document" or "rename_section").ToDictionary(change => change.Id);
            foreach (var change in renameChanges.Values)
            {
                var node = targets[change.Id];
                var proposedTitle = change.NewTitle!.Trim();
                if (data.Nodes.Any(other => other.Id != node.Id && other.IsSection == node.IsSection && other.ParentId == node.ParentId
                    && string.Equals(renameChanges.TryGetValue(other.Id, out var otherChange) ? otherChange.NewTitle!.Trim() : other.Title, proposedTitle, StringComparison.OrdinalIgnoreCase)))
                    return new(false, "В этом разделе уже есть узел с таким названием.");
            }

            foreach (var change in changes)
            {
                var node = targets[change.Id];
                switch (change.Operation)
                {
                    case "rename_document":
                    case "rename_section":
                        node.Title = change.NewTitle!.Trim();
                        break;
                    case "replace_content":
                        node.Content = change.NewContent!;
                        break;
                    case "append_content":
                        var current = node.Content ?? "";
                        var addition = change.NewContent!;
                        node.Content = current.Length == 0 || addition.Length == 0 || current.EndsWith('\n') || addition.StartsWith('\n')
                            ? current + addition
                            : current + "\n" + addition;
                        break;
                }
                node.UpdatedAt = DateTimeOffset.UtcNow;
            }

            data.UpdatedAt = DateTimeOffset.UtcNow;
            await store.WriteAsync(data, ct);
            return new(true, null);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<KnowledgeCreateSectionDocumentResult> CreateSectionAndDocumentIfAbsentAsync(
        string sectionTitle, string documentTitle, string content, Guid? parentId, CancellationToken ct)
    {
        var cleanSection = sectionTitle?.Trim();
        var cleanDocument = documentTitle?.Trim();
        if (string.IsNullOrWhiteSpace(cleanSection) || string.IsNullOrWhiteSpace(cleanDocument))
            return new(false, null, null, "Для создания нужны названия раздела и документа.");

        await mutationGate.WaitAsync(ct);
        try
        {
            var data = await store.ReadAsync(ct);
            if (!ValidParent(data.Nodes, parentId))
                return new(false, null, null, "Родительский раздел больше не существует.");
            if (data.Nodes.Any(node => node.IsSection && node.ParentId == parentId
                && string.Equals(node.Title, cleanSection, StringComparison.OrdinalIgnoreCase)))
                return new(false, null, null, "Раздел с таким названием уже появился. Повторите запрос с актуальными данными.");

            var now = DateTimeOffset.UtcNow;
            var section = new KnowledgeNode { Kind = "section", Title = cleanSection, ParentId = parentId,
                Order = NextOrder(data.Nodes, parentId), CreatedAt = now, UpdatedAt = now };
            var document = new KnowledgeNode { Kind = "document", Title = cleanDocument, Content = content ?? "",
                ParentId = section.Id, Order = 0, CreatedAt = now, UpdatedAt = now };
            data.Nodes.Add(section);
            data.Nodes.Add(document);
            data.UpdatedAt = now;
            Normalize(data.Nodes);
            await store.WriteAsync(data, ct);
            return new(true, section.Id, document.Id, null);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<IReadOnlyList<KnowledgeNodeDto>> GetTreeAsync(CancellationToken ct)
    {
        var data = await store.ReadAsync(ct);
        return BuildTree(data.Nodes);
    }

    public async Task<KnowledgeDocumentDto?> GetDocumentAsync(Guid id, CancellationToken ct)
    {
        var data = await store.ReadAsync(ct);
        var node = data.Nodes.FirstOrDefault(n => n.Id == id && !n.IsSection);
        return node is null ? null : new(node.Id, node.Kind, node.Title, node.Content ?? "", node.CreatedAt, node.UpdatedAt);
    }

    public async Task<KnowledgeSnapshotDto> GetSnapshotAsync(CancellationToken ct)
    {
        var data = await store.ReadAsync(ct);
        var byId = data.Nodes.ToDictionary(node => node.Id);
        string PathFor(KnowledgeNode node)
        {
            var parts = new Stack<string>();
            var current = node;
            var seen = new HashSet<Guid>();
            while (seen.Add(current.Id))
            {
                parts.Push(current.Title);
                if (current.ParentId is not Guid parentId || !byId.TryGetValue(parentId, out current!)) break;
            }
            return string.Join(" / ", parts);
        }
        var nodes = data.Nodes.OrderBy(node => PathFor(node), StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Order)
            .Select(node => new KnowledgeSnapshotNodeDto(node.Id, node.Kind, node.Title, node.ParentId, node.Order, PathFor(node), node.Content, node.CreatedAt, node.UpdatedAt))
            .ToArray();
        return new KnowledgeSnapshotDto(data.SchemaVersion, data.UpdatedAt, nodes);
    }

    public async Task<(KnowledgeNode? Node, string? Error)> CreateAsync(string kind, string? title, string? content, Guid? parentId, CancellationToken ct)
    {
        var cleanTitle = title?.Trim();
        if (kind is not ("section" or "document")) return (null, "Тип узла не поддерживается.");
        await mutationGate.WaitAsync(ct);
        try {
            var data = await store.ReadAsync(ct);
            if (!ValidParent(data.Nodes, parentId)) return (null, "Родительский раздел не найден.");
            if (kind == "document" && string.IsNullOrWhiteSpace(cleanTitle))
            {
                var number = Math.Max(1, data.NextDocumentNumber);
                foreach (var existing in data.Nodes)
                    if (existing.Title.StartsWith("Doc ", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(existing.Title.AsSpan(4), out var existingNumber)
                        && existingNumber >= number)
                        number = existingNumber + 1;
                var titles = data.Nodes.Select(node => node.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                while (titles.Contains($"Doc {number}")) number++;
                cleanTitle = $"Doc {number}";
                data.NextDocumentNumber = number + 1;
            }
            if (kind == "section" && string.IsNullOrWhiteSpace(cleanTitle))
            {
                var number = Math.Max(1, data.NextSectionNumber);
                foreach (var existing in data.Nodes)
                    if (existing.IsSection
                        && existing.Title.StartsWith("Section ", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(existing.Title.AsSpan(8), out var existingNumber)
                        && existingNumber >= number)
                        number = existingNumber + 1;
                var titles = data.Nodes.Where(node => node.IsSection).Select(node => node.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                while (titles.Contains($"Section {number}")) number++;
                cleanTitle = $"Section {number}";
                data.NextSectionNumber = number + 1;
            }
            var now = DateTimeOffset.UtcNow;
            var node = new KnowledgeNode { Kind = kind, Title = cleanTitle!, Content = kind == "document" ? content ?? "" : null, ParentId = parentId, Order = NextOrder(data.Nodes, parentId), CreatedAt = now, UpdatedAt = now };
            data.Nodes.Add(node); Normalize(data.Nodes); await store.WriteAsync(data, ct); return (node, null);
        } finally { mutationGate.Release(); }
    }

    public async Task<string?> RenameAsync(Guid id, string? title, CancellationToken ct)
    {
        var clean = title?.Trim(); if (string.IsNullOrWhiteSpace(clean)) return "Название обязательно.";
        await mutationGate.WaitAsync(ct);
        try { var data = await store.ReadAsync(ct); var node = data.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) return "Узел не найден."; node.Title = clean; node.UpdatedAt = DateTimeOffset.UtcNow; await store.WriteAsync(data, ct); return null;
        } finally { mutationGate.Release(); }
    }

    public async Task<string?> UpdateContentAsync(Guid id, string? content, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try { var data = await store.ReadAsync(ct); var node = data.Nodes.FirstOrDefault(n => n.Id == id && !n.IsSection);
            if (node is null) return "Документ не найден."; node.Content = content ?? ""; node.UpdatedAt = DateTimeOffset.UtcNow; await store.WriteAsync(data, ct); return null;
        } finally { mutationGate.Release(); }
    }

    public async Task<string?> UpdateContentIfCurrentAsync(Guid id, string expectedTitle, string expectedContent, string? content, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            var data = await store.ReadAsync(ct);
            var node = data.Nodes.FirstOrDefault(n => n.Id == id && !n.IsSection);
            if (node is null) return "Документ не найден.";
            if (node.Title != expectedTitle || (node.Content ?? "") != expectedContent)
                return "Содержание документа изменилось после предпросмотра. Запись не выполнена; повторите запрос, чтобы увидеть актуальный текст.";
            node.Content = content ?? "";
            node.UpdatedAt = DateTimeOffset.UtcNow;
            await store.WriteAsync(data, ct);
            return null;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<string?> MoveAsync(Guid id, Guid? parentId, int order, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try { var data = await store.ReadAsync(ct); var node = data.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) return "Узел не найден.";
            if (!ValidParent(data.Nodes, parentId)) return "Родительский раздел не найден.";
            if (parentId == id || (parentId is not null && IsDescendant(data.Nodes, parentId.Value, id))) return "Нельзя переместить узел внутрь самого себя.";
            var siblings = data.Nodes.Where(n => n.ParentId == parentId && n.Id != id).OrderBy(n => n.Order).ToList();
            var insertion = Math.Clamp(order, 0, siblings.Count); node.ParentId = parentId; node.Order = insertion; node.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var (sibling, index) in siblings.Select((item, index) => (item, index))) sibling.Order = index >= insertion ? index + 1 : index;
            Normalize(data.Nodes); await store.WriteAsync(data, ct); return null;
        } finally { mutationGate.Release(); }
    }

    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try { var data = await store.ReadAsync(ct); if (data.Nodes.All(n => n.Id != id)) return "Узел не найден.";
            var ids = new HashSet<Guid> { id }; var changed = true;
            while (changed) { changed = false; foreach (var n in data.Nodes.Where(n => n.ParentId is not null && ids.Contains(n.ParentId.Value))) if (ids.Add(n.Id)) changed = true; }
            data.Nodes.RemoveAll(n => ids.Contains(n.Id)); Normalize(data.Nodes); await store.WriteAsync(data, ct); return null;
        } finally { mutationGate.Release(); }
    }

    private static bool ValidParent(List<KnowledgeNode> nodes, Guid? id) => id is null || nodes.Any(n => n.Id == id && n.IsSection);
    private static int NextOrder(List<KnowledgeNode> nodes, Guid? parent) => nodes.Count(n => n.ParentId == parent);
    private static bool IsDescendant(List<KnowledgeNode> nodes, Guid candidate, Guid ancestor) { var current = nodes.FirstOrDefault(n => n.Id == candidate); while (current?.ParentId is not null) { if (current.ParentId == ancestor) return true; current = nodes.FirstOrDefault(n => n.Id == current.ParentId); } return false; }
    private static void Normalize(List<KnowledgeNode> nodes) { foreach (var group in nodes.GroupBy(n => n.ParentId)) foreach (var (node, index) in group.OrderBy(n => n.Order).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase).Select((n, i) => (n, i))) node.Order = index; }
    private static List<KnowledgeNodeDto> BuildTree(List<KnowledgeNode> nodes) { var byParent = nodes.GroupBy(n => n.ParentId?.ToString() ?? "").ToDictionary(g => g.Key, g => g.OrderBy(n => n.Order).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ToList()); List<KnowledgeNodeDto> Build(Guid? parent) => byParent.TryGetValue(parent?.ToString() ?? "", out var list) ? list.Select(n => new KnowledgeNodeDto(n.Id, n.Kind, n.Title, n.ParentId, n.Order, n.CreatedAt, n.UpdatedAt, n.IsSection ? Build(n.Id) : null)).ToList() : []; return Build(null); }
}
