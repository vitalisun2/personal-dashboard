using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Chat.Api;

public static class ChatApiModule
{
    public static IEndpointRouteBuilder MapChatApi(this IEndpointRouteBuilder endpoints) => endpoints;
}
