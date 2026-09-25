using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PersonalDashboard.V2.Platform.Persistence;

internal sealed class EntityChangeRowConfiguration : IEntityTypeConfiguration<EntityChangeRow>
{
    public void Configure(EntityTypeBuilder<EntityChangeRow> builder)
    {
        builder.ToTable("entity_change_journal", "platform");
        builder.HasKey(row => row.Sequence);
        builder.Property(row => row.Sequence).ValueGeneratedNever();
        builder.Property(row => row.Type).HasMaxLength(160).IsRequired();
        builder.Property(row => row.PayloadJson).HasColumnType("jsonb");
        builder.HasIndex(row => new { row.Type, row.Id, row.Version });
    }
}

internal sealed class EntityChangeCursorRowConfiguration : IEntityTypeConfiguration<EntityChangeCursorRow>
{
    public void Configure(EntityTypeBuilder<EntityChangeCursorRow> builder)
    {
        builder.ToTable("entity_change_cursor", "platform");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.HasData(new EntityChangeCursorRow { Id = 1, Sequence = 0 });
    }
}

internal sealed class SyncOperationResultRowConfiguration : IEntityTypeConfiguration<SyncOperationResultRow>
{
    public void Configure(EntityTypeBuilder<SyncOperationResultRow> builder)
    {
        builder.ToTable("sync_operation_results", "platform");
        builder.HasKey(row => row.OperationId);
        builder.Property(row => row.ResultJson).HasColumnType("jsonb").IsRequired();
    }
}
