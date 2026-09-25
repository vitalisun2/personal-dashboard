using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

internal sealed class KnowledgeNodeConfiguration : IEntityTypeConfiguration<KnowledgeNode>
{
    public void Configure(EntityTypeBuilder<KnowledgeNode> builder)
    {
        builder.ToTable("knowledge_nodes", "knowledge");
        builder.HasKey(node => node.Id);
        builder.Property(node => node.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(node => node.Title).HasMaxLength(300).IsRequired();
        builder.Property(node => node.Markdown).HasColumnType("text").IsRequired();
        builder.Property(node => node.Version).IsConcurrencyToken();
        builder.Property(node => node.UpdatedAt).IsRequired();
        builder.HasIndex(node => new { node.ParentId, node.Position });
        builder.HasIndex(node => node.DeletedAt);
        builder.HasIndex(node => node.ArchivedAt);
        builder.Ignore(node => node.IsDeleted);
        builder.Ignore(node => node.IsArchived);
        builder.Ignore(node => node.IsActive);
    }
}
