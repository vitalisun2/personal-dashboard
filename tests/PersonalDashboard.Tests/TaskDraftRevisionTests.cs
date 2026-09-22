namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskDraftRevisionTests
{
    [TestMethod]
    public async Task LocalAgentRevisionPreservesDraftAndAddsCorrection()
    {
        var agent = new LocalTaskAgent();
        var source = new TaskDraft("Проверить экран", "Проверить модальное окно.", "Личный дашборд");

        var revised = await agent.ReviseDraftAsync(source, "  добавить понятный текст  ", ["Общее", "Личный дашборд"]);

        Assert.AreEqual(source.Title, revised.Title);
        Assert.AreEqual(source.Section, revised.Section);
        StringAssert.Contains(revised.Description, "Проверить модальное окно.");
        StringAssert.Contains(revised.Description, "Уточнение: добавить понятный текст");
    }

    [TestMethod]
    public async Task LocalAgentRevisionCanChangeExplicitTitle()
    {
        var agent = new LocalTaskAgent();
        var source = new TaskDraft("Проверить экран", "Проверить модальное окно.", "Личный дашборд");

        var revised = await agent.ReviseDraftAsync(source, "замени заголовок на Проверить подтверждение задачи", ["Общее"]);

        Assert.AreEqual("Проверить подтверждение задачи", revised.Title);
    }

    [TestMethod]
    public void RevisionPromptKeepsCurrentDraftAndCorrection()
    {
        var draft = new TaskDraft("Заголовок", "Описание", "Общее");
        var prompt = TaskPrompt.BuildRevisionRequest(draft, "Сделай короче");

        StringAssert.Contains(prompt, "Заголовок");
        StringAssert.Contains(prompt, "Сделай короче");
    }
}
