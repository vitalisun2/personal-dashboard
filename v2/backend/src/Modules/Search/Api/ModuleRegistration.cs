using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Search.Api;

public static class SearchApiModule
{
    public static IEndpointRouteBuilder MapSearchApi(this IEndpointRouteBuilder endpoints) => endpoints;
}
