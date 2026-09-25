using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Knowledge;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Knowledge.Application;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

public static class KnowledgeInfrastructureModule
{
    public static IServiceCollection AddKnowledgeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IKnowledgeStore, EfKnowledgeStore>();
        services.AddScoped<KnowledgeService>();
        services.AddScoped<IKnowledgeSearchPublisher, KnowledgeSearchPublisher>();
        services.AddScoped<ISyncMutationHandler, KnowledgeSyncMutationHandler>();
        services.AddScoped<IKnowledgeDocumentAccess, KnowledgeDocumentAccess>();
        services.AddScoped<IKnowledgeAgentAccess, KnowledgeAgentAccess>();
        services.AddScoped<KnowledgeSearchSourceFeed>();
        services.AddScoped<ISearchSourceFeed>(provider => provider.GetRequiredService<KnowledgeSearchSourceFeed>());
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
