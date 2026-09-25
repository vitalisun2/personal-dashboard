using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalDashboard.V2.Chat.Infrastructure.Persistence;

internal sealed class ChatConversationRow
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class ChatTurnRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string UserText { get; set; } = string.Empty;
    public string AssistantText { get; set; } = string.Empty;
    public string ScopeMode { get; set; } = "general";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public long? EntityVersion { get; set; }
    public string RequestedModel { get; set; } = "Gemma";
    public string ActualModel { get; set; } = "Gemma";
    public string ModelRoute { get; set; } = "Default";
    public string? FallbackReason { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class ChatProposalRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public string ActionsJson { get; set; } = "[]";
    public string State { get; set; } = "Pending";
    public Guid? ConfirmationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class ChatConversationConfiguration : IEntityTypeConfiguration<ChatConversationRow>
{
    public void Configure(EntityTypeBuilder<ChatConversationRow> builder)
    {
        builder.ToTable("chat_conversations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(240);
        builder.HasIndex(x => x.UpdatedAtUtc);
    }
}

internal sealed class ChatTurnConfiguration : IEntityTypeConfiguration<ChatTurnRow>
{
    public void Configure(EntityTypeBuilder<ChatTurnRow> builder)
    {
        builder.ToTable("chat_turns");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserText).IsRequired();
        builder.Property(x => x.AssistantText).IsRequired();
        builder.Property(x => x.ScopeMode).HasMaxLength(16).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(80);
        builder.Property(x => x.RequestedModel).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ActualModel).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ModelRoute).HasMaxLength(32).IsRequired();
        builder.Property(x => x.FallbackReason).HasMaxLength(240);
        builder.Property(x => x.SourcesJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc, x.Id });
        builder.HasOne<ChatConversationRow>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChatProposalConfiguration : IEntityTypeConfiguration<ChatProposalRow>
{
    public void Configure(EntityTypeBuilder<ChatProposalRow> builder)
    {
        builder.ToTable("chat_proposals");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ActionsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.State).HasMaxLength(16).IsRequired();
        builder.HasIndex(x => x.TurnId).IsUnique();
        builder.HasIndex(x => new { x.ConversationId, x.State, x.CreatedAtUtc });
        builder.HasOne<ChatConversationRow>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChatTurnRow>().WithMany().HasForeignKey(x => x.TurnId).OnDelete(DeleteBehavior.Cascade);
    }
}
