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
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Search.Api;
using PersonalDashboard.V2.Search.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddPersonalOsV2Platform(builder.Configuration);
builder.Services.AddV2Modules(builder.Configuration);

var app = builder.Build();
await app.Services.ApplyPersonalOsV2MigrationsAsync();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
if (Directory.Exists(app.Environment.WebRootPath))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.MapSyncEndpoints();
app.MapKnowledgeApi();
app.MapPlanningApi();
app.MapTasksApi();
app.MapChatApi();
app.MapAgentApi();
app.MapSearchApi();
if (Directory.Exists(app.Environment.WebRootPath))
{
    app.MapFallbackToFile("index.html");
}
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

