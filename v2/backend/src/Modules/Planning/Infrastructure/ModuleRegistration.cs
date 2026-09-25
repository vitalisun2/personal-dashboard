using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Planning.Application;

namespace PersonalDashboard.V2.Planning.Infrastructure;

public static class PlanningInfrastructureModule
{
    public static IServiceCollection AddPlanningInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPlanningRepository, PlanningRepository>();
        services.AddScoped<PlanningService>();
        services.AddScoped<PlanningAgentAccess>();
        services.AddScoped<IPlanningAgentAccess>(sp => sp.GetRequiredService<PlanningAgentAccess>());
        services.AddScoped<PlanningLinkValidator>();
        services.AddScoped<IPlanningLinkValidator>(sp => sp.GetRequiredService<PlanningLinkValidator>());
        services.AddScoped<IPlanningPathReader>(sp => sp.GetRequiredService<PlanningLinkValidator>());
        services.AddScoped<ISyncMutationHandler, PlanningProjectSyncHandler>();
        services.AddScoped<ISyncMutationHandler, PlanningMilestoneSyncHandler>();
        services.AddScoped<ISyncMutationHandler, PlanningFeatureSyncHandler>();
        services.AddScoped<ISearchSourceFeed, PlanningSearchSourceFeed>();
        return services;
    }
}
