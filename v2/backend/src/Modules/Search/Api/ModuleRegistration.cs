using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Contracts.Search;

namespace PersonalDashboard.V2.Search.Api;

public static class SearchApiModule
{
    public static IEndpointRouteBuilder MapSearchApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v2/search", async (SearchRequest request, ISearchService search, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await search.SearchAsync(request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [exception.Message] });
            }
        });
        return endpoints;
    }
}
