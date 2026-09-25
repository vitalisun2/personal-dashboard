using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Tasks.Application;

namespace PersonalDashboard.V2.Tasks.Infrastructure;

public static class TasksInfrastructureModule
{
    public static IServiceCollection AddTasksInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITasksRepository, TasksRepository>();
        services.AddScoped<TasksService>();
        services.AddScoped<ITaskPlanningLinkUsage, TaskPlanningLinkUsage>();
        services.AddScoped<ITaskPlanningProjectionRefresh, TaskPlanningProjectionRefresh>();
        services.AddScoped<TasksAgentAccess>();
        services.AddScoped<ITasksAgentAccess>(sp => sp.GetRequiredService<TasksAgentAccess>());
        services.AddScoped<ISyncMutationHandler, TaskSyncMutationHandler>();
        services.AddScoped<ISyncMutationHandler, TaskSectionSyncMutationHandler>();
        services.AddScoped<ISearchSourceFeed, TasksSearchSourceFeed>();
        return services;
    }
}
