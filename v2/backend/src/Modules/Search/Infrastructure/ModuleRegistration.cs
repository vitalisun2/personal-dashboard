using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalDashboard.V2.Search.Infrastructure;

public static class SearchInfrastructureModule
{
    public static IServiceCollection AddSearchInfrastructure(this IServiceCollection services, IConfiguration configuration) => services;
}
