namespace PersonalDashboard.V2.Agent.Domain;

public enum ChangeModule
{
    Knowledge,
    Planning,
    Tasks
}

public enum ChangeOperation
{
    Create,
    Update,
    Move,
    Archive,
    Restore,
    Delete,
    SetWorkStatus,
    SetFeatureStatus,
    Reorder
}

public sealed record ChangeTarget
{
    public ChangeModule Module { get; }
    public string EntityType { get; }
    public Guid EntityId { get; }
    public long? ExpectedVersion { get; }

    public ChangeTarget(ChangeModule module, string entityType, Guid entityId, long? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(entityType)) throw new ArgumentException("Entity type is required.", nameof(entityType));
        if (entityId == Guid.Empty) throw new ArgumentException("Entity ID is required.", nameof(entityId));
        if (expectedVersion is < 1) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        Module = module;
        EntityType = entityType.Trim();
        EntityId = entityId;
        ExpectedVersion = expectedVersion;
    }
}

public sealed record ProposedChange(
    Guid Id,
    ChangeOperation Operation,
    ChangeTarget Target,
    string DisplayName,
    string? BeforeJson,
    string AfterJson,
    string Preview)
{
    public static ProposedChange Create(ChangeOperation operation, ChangeTarget target, string displayName,
        string? beforeJson, string afterJson, string preview)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));
        ArgumentException.ThrowIfNullOrWhiteSpace(afterJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(preview);
        return new ProposedChange(Guid.NewGuid(), operation, target, displayName.Trim(), beforeJson, afterJson, preview.Trim());
    }
}
