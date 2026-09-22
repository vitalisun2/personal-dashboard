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
builder.Services.AddAgentChat();
builder.Services.AddSingleton<IMemoryRepository, MemoryRepository>();
builder.Services.AddSingleton<IChatCommandFacade, TaskChatFacade>();
builder.Services.AddSingleton<IChatCommandFacade, KnowledgeChatFacade>();
builder.Services.AddSingleton<IChatResponder, ReadOnlyChatResponder>();

var app = builder.Build();
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
