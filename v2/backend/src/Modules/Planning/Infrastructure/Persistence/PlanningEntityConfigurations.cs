using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalDashboard.V2.Planning.Domain;

namespace PersonalDashboard.V2.Planning.Infrastructure.Persistence;

public sealed class PlanningProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("planning_projects", "planning");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(12000).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
        builder.Property(x => x.Position).IsRequired();
        builder.Property(x => x.IsArchived).IsRequired();
        builder.HasMany(x => x.Milestones).WithOne().HasForeignKey("ProjectId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Milestones).HasField("_milestones").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PlanningOrderedEntityConfiguration : IEntityTypeConfiguration<OrderedEntity>
{
    public void Configure(EntityTypeBuilder<OrderedEntity> builder)
    {
        builder.ToTable("planning_items", "planning");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(12000).IsRequired();
        builder.Property(x => x.Position).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
        builder.HasDiscriminator<string>("ItemKind").HasValue<Milestone>("milestone").HasValue<Feature>("feature");
    }
}

public sealed class PlanningMilestoneConfiguration : IEntityTypeConfiguration<Milestone>
{
    public void Configure(EntityTypeBuilder<Milestone> builder)
    {
        builder.HasMany(x => x.Features).WithOne().HasForeignKey("MilestoneId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Features).HasField("_features").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PlanningTombstoneConfiguration : IEntityTypeConfiguration<PlanningTombstone>
{
    public void Configure(EntityTypeBuilder<PlanningTombstone> builder)
    {
        builder.ToTable("planning_tombstones", "planning");
        builder.HasKey(x => new { x.Type, x.Id });
        builder.Property(x => x.Type).HasMaxLength(40);
        builder.Property(x => x.Version).IsRequired();
    }
}
