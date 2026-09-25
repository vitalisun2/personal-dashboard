using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Planning.Api;

public static class PlanningApiModule
{
    public static IEndpointRouteBuilder MapPlanningApi(this IEndpointRouteBuilder endpoints) => endpoints;
}
