using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalDashboard.V2.Tasks.Domain;

namespace PersonalDashboard.V2.Tasks.Infrastructure.Persistence;

public sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("task_items", "tasks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(20000).IsRequired();
        builder.Property(x => x.Location).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.WorkStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Position).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
        builder.HasIndex(x => new { x.ProjectId, x.MilestoneId, x.FeatureId });
        builder.HasIndex(x => new { x.Location, x.SectionId, x.Position });
        builder.HasOne<TaskSection>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TaskTombstone
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TaskTombstoneConfiguration : IEntityTypeConfiguration<TaskTombstone>
{
    public void Configure(EntityTypeBuilder<TaskTombstone> builder)
    {
        builder.ToTable("task_tombstones", "tasks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
    }
}

public sealed class TaskSectionConfiguration : IEntityTypeConfiguration<TaskSection>
{
    public void Configure(EntityTypeBuilder<TaskSection> builder)
    {
        builder.ToTable("task_sections", "tasks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Location).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Position).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => new { x.Location, x.Name }).IsUnique();
    }
}
