// Агент-редактирование существующей задачи (title/description/section):
// локальный fallback применяет явные правки на замену заголовка и перенос в раздел
// (существующий или новый); остальное сохраняется как уточнение к описанию.
// Промпты закрепляют правило «краткое лаконичное описание без выдуманных деталей».

namespace PersonalDashboard.Tests;

[TestClass]
public sealed class TaskAgentEditTests
{
    [TestMethod]
    public async Task LocalAgentEditMovesToExistingSection()
    {
        var agent = new LocalTaskAgent();
        var current = new TaskDraft("Проверить экран", "Проверить модальное окно.", "Личный дашборд");

        var edited = await agent.EditDraftAsync(current, "перенеси задачу в раздел Workflow", ["Общее", "Личный дашборд", "Workflow"]);

        Assert.AreEqual("Workflow", edited.Section);
        Assert.AreEqual(current.Title, edited.Title);
        Assert.AreEqual(current.Description, edited.Description);
    }

    [TestMethod]
    public async Task LocalAgentEditCreatesNewSection()
    {
        var agent = new LocalTaskAgent();
        var current = new TaskDraft("Проверить экран", "Проверить модальное окно.", "Общее");

        var edited = await agent.EditDraftAsync(current, "создай раздел Дизайн и перенеси туда", ["Общее"]);

        Assert.AreEqual("Дизайн", edited.Section);
    }

    [TestMethod]
    public async Task LocalAgentEditChangesExplicitTitle()
    {
        var agent = new LocalTaskAgent();
        var current = new TaskDraft("Проверить экран", "Проверить модальное окно.", "Общее");

        var edited = await agent.EditDraftAsync(current, "замени заголовок на Проверить подтверждение задачи", ["Общее"]);

        Assert.AreEqual("Проверить подтверждение задачи", edited.Title);
        Assert.AreEqual(current.Description, edited.Description);
        Assert.AreEqual(current.Section, edited.Section);
    }

    [TestMethod]
    public async Task LocalAgentEditKeepsFieldsWhenOnlyDescriptionRequested()
    {
        var agent = new LocalTaskAgent();
        var current = new TaskDraft("Проверить экран", "Проверить модальное окно.", "Личный дашборд");

        var edited = await agent.EditDraftAsync(current, "сократи описание", ["Общее", "Личный дашборд"]);

        Assert.AreEqual(current.Title, edited.Title);
        Assert.AreEqual(current.Section, edited.Section);
        StringAssert.Contains(edited.Description, "Проверить модальное окно.");
        StringAssert.Contains(edited.Description, "Уточнение: сократи описание");
    }

    [TestMethod]
    public void EditPromptCarriesCurrentTaskAndInstruction()
    {
        var current = new TaskDraft("Заголовок", "Описание", "Общее");
        var prompt = TaskPrompt.BuildEditRequest(current, "перенеси в Workflow и сократи");

        StringAssert.Contains(prompt, "Заголовок");
        StringAssert.Contains(prompt, "перенеси в Workflow и сократи");
        StringAssert.Contains(prompt, "раздел");
    }

    [TestMethod]
    public void InstructionsRequireConciseDescriptionWithoutExtraDetails()
    {
        StringAssert.Contains(TaskPrompt.Instructions, "лаконичн");
        StringAssert.Contains(TaskPrompt.Instructions, "ничего не добавляя");
        StringAssert.Contains(TaskPrompt.Instructions, "ничего не теряя");
    }

    [TestMethod]
    public void RevisionPromptMentionsSectionMove()
    {
        var draft = new TaskDraft("Заголовок", "Описание", "Общее");
        var prompt = TaskPrompt.BuildRevisionRequest(draft, "перенеси в Workflow");

        StringAssert.Contains(prompt, "перенести задачу в другой раздел");
    }
}
