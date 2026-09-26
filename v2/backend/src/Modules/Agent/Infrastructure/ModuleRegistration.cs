using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Agent.Application;

namespace PersonalDashboard.V2.Agent.Infrastructure;

public static class AgentInfrastructureModule
{
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(nameof(OllamaChatModelProvider));
        services.AddSingleton<IChatModelProvider, OllamaChatModelProvider>();
        services.AddSingleton<IChatModelRouter, ChatModelRouter>();
        services.AddScoped<IAgentTurnService, AgentTurnService>();
        services.AddScoped<IProposalConfirmationService, ProposalConfirmationService>();
        return services;
    }
}
