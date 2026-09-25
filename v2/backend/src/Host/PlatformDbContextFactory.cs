using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Host;

// EF tooling must load module Infrastructure assemblies before building the model.
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        foreach (var name in typeof(PlatformDbContextFactory).Assembly.GetReferencedAssemblies()
                     .Where(name => name.Name is { } assemblyName
                         && assemblyName.StartsWith("PersonalDashboard.V2.", StringComparison.Ordinal)
                         && assemblyName.EndsWith(".Infrastructure", StringComparison.Ordinal)))
        {
            Assembly.Load(name);
        }

        // The fallback is used only to generate migrations; no connection is opened.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PersonalOsV2")
            ?? "Host=localhost;Database=personal_os_v2;Username=design_time;Password=design_time";
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.MigrationsAssembly("PersonalDashboard.V2.Host"))
            .Options;
        return new PlatformDbContext(options);
    }
}
