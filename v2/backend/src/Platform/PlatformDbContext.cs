using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Platform.Persistence;

namespace PersonalDashboard.V2.Platform;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<EntityChangeRow> EntityChanges => Set<EntityChangeRow>();

    public DbSet<EntityChangeCursorRow> EntityChangeCursors => Set<EntityChangeCursorRow>();

    public DbSet<SyncOperationResultRow> SyncOperationResults => Set<SyncOperationResultRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => IsInfrastructureAssembly(assembly.GetName()))
            .Append(typeof(PlatformDbContext).Assembly)
            .Distinct()
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }

    private static bool IsInfrastructureAssembly(AssemblyName name) =>
        name.Name is { } assemblyName
        && assemblyName.StartsWith("PersonalDashboard.V2.", StringComparison.Ordinal)
        && assemblyName.EndsWith(".Infrastructure", StringComparison.Ordinal);
}
