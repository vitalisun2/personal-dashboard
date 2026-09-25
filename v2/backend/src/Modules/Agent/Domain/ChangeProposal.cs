namespace PersonalDashboard.V2.Agent.Domain;

public enum ProposalStatus
{
    Pending,
    Applied,
    Rejected,
    Expired,
    Stale
}

public sealed class ChangeProposal
{
    private readonly List<ProposedChange> _changes = [];

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid TurnId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public ProposalStatus Status { get; private set; }
    public IReadOnlyList<ProposedChange> Changes => _changes;

    private ChangeProposal() { }

    public static ChangeProposal Prepare(Guid conversationId, Guid turnId, string idempotencyKey,
        IEnumerable<ProposedChange> changes, TimeSpan? lifetime = null)
    {
        if (conversationId == Guid.Empty) throw new ArgumentException("Conversation ID is required.", nameof(conversationId));
        if (turnId == Guid.Empty) throw new ArgumentException("Turn ID is required.", nameof(turnId));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(changes);
        var materialized = changes.ToArray();
        if (materialized.Length == 0) throw new ArgumentException("At least one change is required.", nameof(changes));
        if (materialized.Select(x => x.Id).Distinct().Count() != materialized.Length)
            throw new ArgumentException("Change IDs must be unique.", nameof(changes));

        var now = DateTimeOffset.UtcNow;
        var duration = lifetime ?? TimeSpan.FromMinutes(10);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var proposal = new ChangeProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TurnId = turnId,
            IdempotencyKey = idempotencyKey.Trim(),
            CreatedAt = now,
            ExpiresAt = now + duration,
            Status = ProposalStatus.Pending
        };
        proposal._changes.AddRange(materialized);
        return proposal;
    }

    public void MarkApplied()
    {
        EnsurePending();
        Status = ProposalStatus.Applied;
    }

    public void MarkRejected()
    {
        EnsurePending();
        Status = ProposalStatus.Rejected;
    }

    public void MarkStale()
    {
        EnsurePending();
        Status = ProposalStatus.Stale;
    }

    public void Expire(DateTimeOffset now)
    {
        if (Status == ProposalStatus.Pending && now >= ExpiresAt) Status = ProposalStatus.Expired;
    }

    private void EnsurePending()
    {
        if (Status != ProposalStatus.Pending) throw new InvalidOperationException("Only a pending proposal can change status.");
        if (DateTimeOffset.UtcNow >= ExpiresAt)
        {
            Status = ProposalStatus.Expired;
            throw new InvalidOperationException("The proposal has expired.");
        }
    }
}
