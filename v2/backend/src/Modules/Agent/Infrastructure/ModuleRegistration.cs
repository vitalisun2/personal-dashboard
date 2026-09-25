using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalDashboard.V2.Agent.Infrastructure;

public static class AgentInfrastructureModule
{
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration configuration) => services;
}
