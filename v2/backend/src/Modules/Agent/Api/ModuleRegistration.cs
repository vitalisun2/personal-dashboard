using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Agent.Api;

public static class AgentApiModule
{
    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder endpoints) => endpoints;
}
