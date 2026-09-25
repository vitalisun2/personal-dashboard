using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalDashboard.V2.Tasks.Infrastructure;

public static class TasksInfrastructureModule
{
    public static IServiceCollection AddTasksInfrastructure(this IServiceCollection services, IConfiguration configuration) => services;
}
