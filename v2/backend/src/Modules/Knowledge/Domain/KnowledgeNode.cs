namespace PersonalDashboard.V2.Knowledge.Domain;

public enum KnowledgeNodeType
{
    Section,
    Document
}

/// <summary>A section or Markdown document in the user's knowledge tree.</summary>
public sealed class KnowledgeNode
{
    private KnowledgeNode() { }

    public Guid Id { get; private set; }
    public KnowledgeNodeType Type { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Markdown { get; private set; } = string.Empty;
    public Guid? ParentId { get; private set; }
    public int Position { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public bool IsDeleted => DeletedAt.HasValue;
    public bool IsArchived => ArchivedAt.HasValue;
    public bool IsActive => !IsDeleted && !IsArchived;

    public static KnowledgeNode Create(KnowledgeNodeType type, string title, Guid? parentId, int position, DateTimeOffset now, Guid? id = null, string markdown = "")
    {
        ValidateTitle(title);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        return new KnowledgeNode
        {
            Id = id ?? Guid.NewGuid(), Type = type, Title = title.Trim(), Markdown = type == KnowledgeNodeType.Document ? markdown : string.Empty, ParentId = parentId,
            Position = position, Version = 1, UpdatedAt = now
        };
    }

    public void Update(string title, string markdown, long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion);
        if (Type != KnowledgeNodeType.Document && !string.IsNullOrEmpty(markdown))
            throw new InvalidOperationException("Sections cannot contain document content.");
        ValidateTitle(title);
        Title = title.Trim();
        Markdown = markdown ?? string.Empty;
        Touch(now);
    }

    public void EditDocument(string? title, string? markdown, long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion);
        if (Type != KnowledgeNodeType.Document) throw new InvalidOperationException("Only documents can be edited.");
        if (title is not null)
        {
            ValidateTitle(title);
            Title = title.Trim();
        }
        if (markdown is not null) Markdown = markdown;
        Touch(now);
    }

    public bool ApplySyncState(string title, string markdown, Guid? parentId, int position, long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion);
        ValidateTitle(title);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        if (Type == KnowledgeNodeType.Section && !string.IsNullOrEmpty(markdown))
            throw new InvalidOperationException("Sections cannot contain document content.");
        markdown ??= string.Empty;
        Title = title.Trim();
        Markdown = Type == KnowledgeNodeType.Document ? markdown : string.Empty;
        ParentId = parentId;
        Position = position;
        Touch(now);
        return true;
    }

    public void Rename(string title, long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion);
        ValidateTitle(title);
        Title = title.Trim();
        Touch(now);
    }

    public void Move(Guid? parentId, int position, long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        ParentId = parentId;
        Position = position;
        Touch(now);
    }

    public void Delete(long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion, allowArchived: true);
        DeletedAt = now;
        Touch(now);
    }

    public void Archive(long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion);
        ArchivedAt = now;
        Touch(now);
    }

    public void Restore(long expectedVersion, DateTimeOffset now)
    {
        EnsureVersion(expectedVersion, allowArchived: true);
        if (!IsArchived) throw new InvalidOperationException("Knowledge node is not archived.");
        ArchivedAt = null;
        Touch(now);
    }

    public void SetPosition(int position, DateTimeOffset now)
    {
        if (Position == position) return;
        Position = position;
        Touch(now);
    }

    public void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    public void EnsureVersion(long expectedVersion, bool allowArchived = false)
    {
        if (IsDeleted) throw new KnowledgeNodeDeletedException(Id);
        if (IsArchived && !allowArchived) throw new KnowledgeNodeArchivedException(Id);
        if (Version != expectedVersion) throw new KnowledgeVersionConflictException(Id, expectedVersion, Version);
    }

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A title is required.", nameof(title));
        if (title.Trim().Length > 300) throw new ArgumentException("Titles cannot exceed 300 characters.", nameof(title));
    }
}

public sealed class KnowledgeVersionConflictException(Guid id, long expected, long actual)
    : InvalidOperationException($"Knowledge node {id} has version {actual}; expected {expected}.")
{
    public Guid NodeId { get; } = id;
    public long ExpectedVersion { get; } = expected;
    public long ActualVersion { get; } = actual;
}

public sealed class KnowledgeNodeDeletedException(Guid id)
    : InvalidOperationException($"Knowledge node {id} has been deleted.")
{
    public Guid NodeId { get; } = id;
}

public sealed class KnowledgeNodeArchivedException(Guid id)
    : InvalidOperationException($"Knowledge node {id} is archived.")
{
    public Guid NodeId { get; } = id;
}

public sealed class KnowledgeConcurrentWriteException(Guid id)
    : InvalidOperationException($"Knowledge node {id} changed concurrently; reload it and retry.")
{
    public Guid NodeId { get; } = id;
}
