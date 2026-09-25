using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Contracts.Transactions;

namespace PersonalDashboard.V2.Platform;

public static class PlatformRegistration
{
    public static IServiceCollection AddPersonalOsV2Platform(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PersonalOsV2")
            ?? throw new InvalidOperationException("Connection string 'PersonalOsV2' is required.");

        services.AddDbContext<PlatformDbContext>(options => options.UseNpgsql(
            connectionString,
            postgres => postgres.MigrationsAssembly("PersonalDashboard.V2.Host")));
        services.AddScoped<IEntityChangeJournal, EfEntityChangeJournal>();
        services.AddScoped<ISyncOperationJournal, EfSyncOperationJournal>();
        services.AddScoped<ITransactionRunner, EfTransactionRunner>();
        return services;
    }

    public static async Task ApplyPersonalOsV2MigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
