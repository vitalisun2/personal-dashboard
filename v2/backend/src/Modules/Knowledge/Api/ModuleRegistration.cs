using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Knowledge.Api;

public static class KnowledgeApiModule
{
    public static IEndpointRouteBuilder MapKnowledgeApi(this IEndpointRouteBuilder endpoints) => endpoints;
}
