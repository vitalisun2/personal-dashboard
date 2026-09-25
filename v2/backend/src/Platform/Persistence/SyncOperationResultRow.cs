namespace PersonalDashboard.V2.Platform.Persistence;

public sealed class SyncOperationResultRow
{
    public Guid OperationId { get; set; }
    public required string ResultJson { get; set; }
}
