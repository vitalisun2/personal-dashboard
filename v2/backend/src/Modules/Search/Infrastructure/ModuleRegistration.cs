using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Search.Application;

namespace PersonalDashboard.V2.Search.Infrastructure;

public static class SearchInfrastructureModule
{
    public static IServiceCollection AddSearchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<OllamaEmbeddingClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<ISearchIndexer, PostgresSearchIndexer>();
        services.AddScoped<ISearchCandidateStore, PostgresSearchCandidateStore>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<SearchIndexRebuilder>();
        services.AddHostedService<SearchSchemaInitializer>();
        services.AddHostedService<SearchEmbeddingWorker>();
        services.AddHostedService<SearchJournalCatchUpWorker>();
        return services;
    }
}
