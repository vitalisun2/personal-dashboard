using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalDashboard.V2.Planning.Infrastructure;

public static class PlanningInfrastructureModule
{
    public static IServiceCollection AddPlanningInfrastructure(this IServiceCollection services, IConfiguration configuration) => services;
}
