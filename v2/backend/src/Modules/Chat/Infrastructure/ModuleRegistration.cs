using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;

namespace PersonalDashboard.V2.Chat.Infrastructure;

public static class ChatInfrastructureModule
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IChatConversationStore, EfChatConversationStore>();
        services.AddScoped<ChatSearchIndex>();
        services.AddScoped<ISearchSourceFeed>(provider => provider.GetRequiredService<ChatSearchIndex>());
        return services;
    }
}
