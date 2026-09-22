using KnowledgeBase.Api.Domain;

namespace KnowledgeBase.Api.Application;

public sealed record KnowledgeNodeDto(Guid Id, string Kind, string Title, Guid? ParentId, int Order, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<KnowledgeNodeDto>? Children = null);
public sealed record KnowledgeDocumentDto(Guid Id, string Kind, string Title, string Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateSectionRequest(string? Title, Guid? ParentId);
public sealed record CreateDocumentRequest(string? Title, string? Content, Guid? ParentId);
public sealed record RenameRequest(string? Title);
public sealed record UpdateContentRequest(string? Content);
public sealed record MoveRequest(Guid? ParentId, int Order);

public interface IKnowledgeStore
{
    Task<KnowledgeDocument> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(KnowledgeDocument document, CancellationToken cancellationToken);
}

public sealed class KnowledgeService(IKnowledgeStore store)
{
    private readonly SemaphoreSlim mutationGate = new(1, 1);
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

    public async Task<(KnowledgeNode? Node, string? Error)> CreateAsync(string kind, string? title, string? content, Guid? parentId, CancellationToken ct)
    {
        var cleanTitle = title?.Trim();
        if (kind != "document" && string.IsNullOrWhiteSpace(cleanTitle)) return (null, "Название обязательно.");
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
