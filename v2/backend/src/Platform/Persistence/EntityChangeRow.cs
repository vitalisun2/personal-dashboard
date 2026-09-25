namespace PersonalDashboard.V2.Platform.Persistence;

public sealed class EntityChangeRow
{
    public long Sequence { get; set; }
    public required string Type { get; set; }
    public Guid Id { get; set; }
    public long Version { get; set; }
    public bool Deleted { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class EntityChangeCursorRow
{
    public int Id { get; set; }
    public long Sequence { get; set; }
}
