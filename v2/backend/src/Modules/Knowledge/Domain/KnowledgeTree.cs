namespace PersonalDashboard.V2.Knowledge.Domain;

/// <summary>Tree invariants and path calculations over the live node set.</summary>
public static class KnowledgeTree
{
    public static IReadOnlyList<KnowledgeNode> DescendantsAndSelf(IReadOnlyCollection<KnowledgeNode> nodes, Guid id)
    {
        var result = new List<KnowledgeNode>();
        Visit(id);
        return result;

        void Visit(Guid current)
        {
            var node = nodes.FirstOrDefault(n => n.Id == current && n.IsActive)
                ?? throw new KeyNotFoundException($"Knowledge node {current} was not found.");
            result.Add(node);
            foreach (var child in nodes.Where(n => n.ParentId == current && n.IsActive).OrderBy(n => n.Position))
                Visit(child.Id);
        }
    }

    public static IReadOnlyList<KnowledgeNode> ArchivedDescendantsAndSelf(IReadOnlyCollection<KnowledgeNode> nodes, Guid id)
    {
        var result = new List<KnowledgeNode>();
        Visit(id);
        return result;

        void Visit(Guid current)
        {
            var node = nodes.FirstOrDefault(n => n.Id == current && n.IsArchived && !n.IsDeleted)
                ?? throw new KeyNotFoundException($"Archived Knowledge node {current} was not found.");
            result.Add(node);
            foreach (var child in nodes.Where(n => n.ParentId == current && n.IsArchived && !n.IsDeleted).OrderBy(n => n.Position))
                Visit(child.Id);
        }
    }

    public static IReadOnlyList<KnowledgeNode> NonDeletedDescendantsAndSelf(IReadOnlyCollection<KnowledgeNode> nodes, Guid id)
    {
        var result = new List<KnowledgeNode>();
        Visit(id);
        return result;

        void Visit(Guid current)
        {
            var node = nodes.FirstOrDefault(n => n.Id == current && !n.IsDeleted)
                ?? throw new KeyNotFoundException($"Knowledge node {current} was not found.");
            result.Add(node);
            foreach (var child in nodes.Where(n => n.ParentId == current && !n.IsDeleted).OrderBy(n => n.Position))
                Visit(child.Id);
        }
    }

    public static string GetPath(IReadOnlyCollection<KnowledgeNode> nodes, KnowledgeNode node)
    {
        var titles = new Stack<string>();
        var current = node;
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(current.Id)) throw new InvalidOperationException("Knowledge tree contains a cycle.");
            titles.Push(current.Title);
            if (current.ParentId is not Guid parentId) break;
            current = nodes.FirstOrDefault(n => n.Id == parentId)
                ?? throw new InvalidOperationException("Knowledge node has a missing parent.");
        }
        return string.Join(" / ", titles);
    }

    public static void EnsureValidParent(IReadOnlyCollection<KnowledgeNode> nodes, Guid? parentId, Guid? movingId = null)
    {
        if (parentId is null) return;
        var parent = nodes.FirstOrDefault(n => n.Id == parentId && n.IsActive)
            ?? throw new KeyNotFoundException($"Parent section {parentId} was not found.");
        if (parent.Type != KnowledgeNodeType.Section)
            throw new InvalidOperationException("Documents can only be placed inside sections.");
        if (movingId is Guid id && (parent.Id == id || DescendantsAndSelf(nodes, id).Any(n => n.Id == parent.Id)))
            throw new InvalidOperationException("A node cannot be moved into itself or its descendant.");
    }
}
