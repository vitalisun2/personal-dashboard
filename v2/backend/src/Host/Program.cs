using PersonalDashboard.V2.Knowledge.Api;
using PersonalDashboard.V2.Knowledge.Infrastructure;
using PersonalDashboard.V2.Planning.Api;
using PersonalDashboard.V2.Planning.Infrastructure;
using PersonalDashboard.V2.Tasks.Api;
using PersonalDashboard.V2.Tasks.Infrastructure;
using PersonalDashboard.V2.Chat.Api;
using PersonalDashboard.V2.Chat.Infrastructure;
using PersonalDashboard.V2.Agent.Api;
using PersonalDashboard.V2.Agent.Infrastructure;
using PersonalDashboard.V2.Search.Api;
using PersonalDashboard.V2.Search.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddV2Modules(builder.Configuration);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapKnowledgeApi();
app.MapPlanningApi();
app.MapTasksApi();
app.MapChatApi();
app.MapAgentApi();
app.MapSearchApi();
app.Run();

static class V2ModuleRegistration
{
    public static IServiceCollection AddV2Modules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKnowledgeInfrastructure(configuration);
        services.AddPlanningInfrastructure(configuration);
        services.AddTasksInfrastructure(configuration);
        services.AddChatInfrastructure(configuration);
        services.AddAgentInfrastructure(configuration);
        services.AddSearchInfrastructure(configuration);
        return services;
    }
}

