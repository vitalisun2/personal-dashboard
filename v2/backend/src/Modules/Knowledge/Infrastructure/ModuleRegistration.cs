using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

public static class KnowledgeInfrastructureModule
{
    public static IServiceCollection AddKnowledgeInfrastructure(this IServiceCollection services, IConfiguration configuration) => services;
}
