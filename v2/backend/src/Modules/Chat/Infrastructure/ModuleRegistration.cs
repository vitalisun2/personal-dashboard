using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalDashboard.V2.Chat.Infrastructure;

public static class ChatInfrastructureModule
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services, IConfiguration configuration) => services;
}
