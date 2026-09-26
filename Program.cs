using System.Text.Json;
using System.Text.Json.Serialization;
using AgentChat;
using KnowledgeBase.Api;
using TaskBoard;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

// The host is intentionally only the composition root. Each module owns its data and HTTP contract.
builder.Services.AddTaskBoard();
builder.Services.AddKnowledgeBase();
builder.Services.AddHttpClient("V2Peer");
builder.Services.AddSingleton<V1SyncOutbox>();
builder.Services.AddSingleton<TaskBoard.Infrastructure.ITaskSyncOutbox>(services => services.GetRequiredService<V1SyncOutbox>());
builder.Services.AddSingleton<KnowledgeBase.Api.Application.IKnowledgeSyncOutbox>(services => services.GetRequiredService<V1SyncOutbox>());
builder.Services.AddHostedService(services => services.GetRequiredService<V1SyncOutbox>());
builder.Services.AddAgentChat();
builder.Services.AddSingleton<IMemoryRepository, MemoryRepository>();
builder.Services.AddSingleton<IChatCommandFacade, TaskChatFacade>();
builder.Services.AddSingleton<IChatCommandFacade, KnowledgeChatFacade>();
builder.Services.AddSingleton<IChatResponder, ReadOnlyChatResponder>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/sync/v2"))
    {
        var secret = app.Configuration["V1_V2_SYNC_KEY"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var supplied = context.Request.Headers["X-PersonalDashboard-Sync-Key"].ToString();
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        if (expectedBytes.Length != suppliedBytes.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTaskBoardApi();
app.MapKnowledgeBaseApi();
app.MapAgentChat();
app.MapLegacyTaskChatApi();

app.MapGet("/api/memory/dashboard", async (IMemoryRepository memory) => Results.Ok(await memory.GetDashboard()));
app.MapGet("/api/memory/status", async (IMemoryRepository memory) => Results.Ok(await memory.GetStatus()));
app.MapGet("/api/memory/projects", async (IMemoryRepository memory) => Results.Ok(await memory.GetProjects()));
app.MapGet("/api/memory/agents", async (IMemoryRepository memory) => Results.Ok(await memory.GetAgents()));
app.MapGet("/api/memory/lessons", async (string? q, string? project, string? agent, IMemoryRepository memory) => Results.Ok(await memory.SearchLessons(q, project, agent)));
app.MapGet("/api/memory/problems", async (string? q, string? project, IMemoryRepository memory) => Results.Ok(await memory.SearchProblems(q, project)));
app.MapGet("/api/memory/skills", async (string? q, IMemoryRepository memory) => Results.Ok(await memory.SearchSkills(q)));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
