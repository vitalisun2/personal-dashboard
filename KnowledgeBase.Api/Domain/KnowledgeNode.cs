namespace KnowledgeBase.Api.Domain;

public sealed class KnowledgeNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "section";
    public string Title { get; set; } = "";
    public Guid? ParentId { get; set; }
    public int Order { get; set; }
    public string? Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsSection => Kind == "section";
}

public sealed class KnowledgeDocument
{
    public int SchemaVersion { get; set; } = 1;
    public int NextDocumentNumber { get; set; } = 1;
    public List<KnowledgeNode> Nodes { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
