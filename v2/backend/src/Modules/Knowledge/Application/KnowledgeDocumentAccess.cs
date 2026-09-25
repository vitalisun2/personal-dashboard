using PersonalDashboard.V2.Contracts.Knowledge;
using PersonalDashboard.V2.Knowledge.Domain;

namespace PersonalDashboard.V2.Knowledge.Application;

/// <summary>Version-checked document access exposed to the Agent module.</summary>
public sealed class KnowledgeDocumentAccess(KnowledgeService knowledge) : IKnowledgeDocumentAccess
{
    public Task<KnowledgeDocumentState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        knowledge.GetDocumentStateAsync(id, cancellationToken);

    public async Task<KnowledgeDocumentEditResult> EditAsync(KnowledgeDocumentEdit edit, CancellationToken cancellationToken = default)
    {
        try
        {
            await knowledge.EditDocumentAsync(edit.Id, edit.Title, edit.Markdown, edit.ExpectedVersion, cancellationToken);
            return new KnowledgeDocumentEditResult(true, await knowledge.GetDocumentStateAsync(edit.Id, cancellationToken), null);
        }
        catch (KnowledgeVersionConflictException conflict)
        {
            return new KnowledgeDocumentEditResult(false, await knowledge.GetDocumentStateAsync(edit.Id, cancellationToken), conflict.Message);
        }
        catch (KnowledgeConcurrentWriteException conflict)
        {
            return new KnowledgeDocumentEditResult(false, await knowledge.GetDocumentStateAsync(edit.Id, cancellationToken), conflict.Message);
        }
        catch (KeyNotFoundException missing)
        {
            return new KnowledgeDocumentEditResult(false, null, missing.Message);
        }
        catch (ArgumentException invalid)
        {
            return new KnowledgeDocumentEditResult(false, await knowledge.GetDocumentStateAsync(edit.Id, cancellationToken), invalid.Message);
        }
        catch (InvalidOperationException invalid)
        {
            return new KnowledgeDocumentEditResult(false, await knowledge.GetDocumentStateAsync(edit.Id, cancellationToken), invalid.Message);
        }
    }
}
