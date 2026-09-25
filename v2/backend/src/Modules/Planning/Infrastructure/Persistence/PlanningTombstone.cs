namespace PersonalDashboard.V2.Planning.Infrastructure.Persistence;

public sealed class PlanningTombstone
{
    public string Type { get; set; } = "";
    public Guid Id { get; set; }
    public long Version { get; set; }
}
