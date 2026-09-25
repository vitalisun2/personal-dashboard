using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PersonalDashboard.V2.Tasks.Api;

public static class TasksApiModule
{
    public static IEndpointRouteBuilder MapTasksApi(this IEndpointRouteBuilder endpoints) => endpoints;
}
