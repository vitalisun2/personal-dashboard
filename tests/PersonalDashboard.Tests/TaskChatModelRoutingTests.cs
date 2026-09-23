using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard;
using TaskBoard.Application;
using TaskBoard.Domain;
using TaskBoard.Infrastructure;
using BoardStatus = TaskBoard.Domain.TaskStatus;

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskChatModelRoutingTests
{
    [TestMethod]
    public async Task GemmaModeCallsOnlyLocalProvider()
    {
        var deepSeek = new StubProvider("OpenRouter (deepseek)", "ответ DeepSeek");
        var gemma = new StubProvider("Ollama (gemma4)", "ответ Gemma");
        var agent = new LlmTaskAgent([deepSeek, gemma], new LocalTaskAgent(), NullLogger<LlmTaskAgent>.Instance);

        var result = await ((IModelSelectableTaskAgent)agent).ChatAsync("вопрос", null, true);

        Assert.AreEqual("ответ Gemma", result);
        Assert.AreEqual(0, deepSeek.ChatCalls);
        Assert.AreEqual(1, gemma.ChatCalls);
    }

    [TestMethod]
    public async Task DeepSeekModeFallsBackToGemmaWhenRouterIsUnavailable()
    {
        var deepSeek = new StubProvider("OpenRouter (deepseek)", null);
        var gemma = new StubProvider("Ollama (gemma4)", "ответ Gemma");
        var agent = new LlmTaskAgent([deepSeek, gemma], new LocalTaskAgent(), NullLogger<LlmTaskAgent>.Instance);

        var result = await ((IModelSelectableTaskAgent)agent).ChatAsync("вопрос", null, false);

        Assert.AreEqual("ответ Gemma", result);
        Assert.AreEqual(1, deepSeek.ChatCalls);
        Assert.AreEqual(1, gemma.ChatCalls);
    }

    [TestMethod]
    public async Task DeepSeekReceivesLargeFullContextEvenWhenGemmaLimitIsExceeded()
    {
        var deepSeek = new StubProvider("OpenRouter (deepseek)", "ответ DeepSeek");
        var gemma = new StubProvider("Ollama (gemma4)", "ответ Gemma");
        var agent = new LlmTaskAgent([deepSeek, gemma], new LocalTaskAgent(), NullLogger<LlmTaskAgent>.Instance);
        var context = new[] { new TaskConversationMessage("system", new string('x', 32_001)) };

        var result = await ((IModelSelectableTaskAgent)agent).ResolveChatActionAsync(context, gemmaOnly: false);

        Assert.AreEqual("ответ DeepSeek", result);
        Assert.AreEqual(1, deepSeek.ChatCalls);
        Assert.AreEqual(0, gemma.ChatCalls);
    }

    [TestMethod]
    public async Task GemmaUnavailableDoesNotUseDeterministicAgent()
    {
        var gemma = new StubProvider("Ollama (gemma4)", null);
        var agent = new LlmTaskAgent([gemma], new LocalTaskAgent(), NullLogger<LlmTaskAgent>.Instance);

        await Assert.ThrowsAsync<ChatModelUnavailableException>(async () =>
            await ((IModelSelectableTaskAgent)agent).ChatAsync("вопрос", null, true));
        Assert.AreEqual(1, gemma.ChatCalls);
    }

    [TestMethod]
    public async Task DeepSeekAndGemmaUnavailableCannotCreateOrEditThroughChat()
    {
        var path = Path.Combine(Path.GetTempPath(), "dashboard-model-routing-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new TaskStore(path);
            var existing = new TaskItem(Guid.NewGuid(), "Заметки о декоре", "Сохранить материалы", "Работа", TaskBucket.Backlog, BoardStatus.New, DateTimeOffset.UtcNow);
            await store.AddAsync(existing);
            var agent = new LlmTaskAgent(
                [new StubProvider("OpenRouter (deepseek)", null), new StubProvider("Ollama (gemma4)", null)],
                new LocalTaskAgent(), NullLogger<LlmTaskAgent>.Instance);
            var service = new TaskChatService(store, agent);

            await Assert.ThrowsAsync<ChatModelUnavailableException>(async () =>
                await service.HandleAsync("Создай задачу: записать идеи о декоре", initialTaskEntry: true, gemmaOnly: false));
            await Assert.ThrowsAsync<ChatModelUnavailableException>(async () =>
                await service.HandleAsync("Допиши описание задачи про декор: добавить примеры", gemmaOnly: false));

            var remaining = await store.GetAllAsync();
            Assert.HasCount(1, remaining);
            Assert.AreEqual("Сохранить материалы", remaining.Single().Description);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class StubProvider(string name, string? reply) : ILlmProvider
    {
        public string Name => name;
        public int ChatCalls { get; private set; }
        public Task<TaskDraft?> TryParseAsync(string rawText, IReadOnlyCollection<string> existingSections) => Task.FromResult<TaskDraft?>(null);
        public Task<string?> TryChatAsync(IReadOnlyList<TaskConversationMessage> history) { ChatCalls++; return Task.FromResult(reply); }
    }
}
