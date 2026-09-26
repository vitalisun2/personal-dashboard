using System.Text.Json;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Knowledge;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Agent.Application;

public sealed record AgentScope(string Mode, string? EntityType, Guid? EntityId, long? EntityVersion);

public sealed record AgentTurnRequest(
    Guid ConversationId,
    Guid TurnId,
    string Prompt,
    AgentScope Scope,
    string RequestedModel,
    ChatModelRoute RequestedRoute,
    IReadOnlyList<ModelMessage> RecentMessages);

public sealed record AgentTurnResult(
    string Answer,
    AgentScope Scope,
    string RequestedModel,
    string ActualModel,
    ChatModelRoute ModelRoute,
    string? FallbackReason,
    ChangeProposal? Proposal,
    IReadOnlyList<SearchSourceReference> Sources);

public interface IAgentTurnService
{
    Task<AgentTurnResult> RespondAsync(AgentTurnRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentTurnService(
    IChatModelRouter models,
    ISearchService search,
    IKnowledgeAgentAccess knowledgeAgent,
    IPlanningAgentAccess planning,
    ITasksAgentAccess tasks) : IAgentTurnService
{
    private const int MaxToolRounds = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ModelTool[] Tools =
    [
        new("search_app", "Search only the Planning or Tasks section for questions and lookups; returned currentEntity values are current context. Do not use this for Knowledge search: direct the user to the dedicated Knowledge section search. If the section is unclear, ask the user.", """
        {"type":"object","properties":{"query":{"type":"string"},"section":{"type":"string","enum":["planning","tasks"]},"exhaustive":{"type":"boolean"}},"required":["query","section"],"additionalProperties":false}
        """),
        new("propose_changes", "Prepare exactly one new knowledge.document or tasks.task for user review. Never update existing data. If section, object kind, title, or document markdown is unclear, ask the user instead of proposing.", """
        {"type":"object","properties":{"changes":{"type":"array","minItems":1,"maxItems":1,"items":{"type":"object","properties":{"module":{"type":"string","enum":["Knowledge","Tasks"]},"operation":{"type":"string","enum":["Create"]},"entityType":{"type":"string","enum":["knowledge.document","tasks.task"]},"after":{"type":"object","properties":{"title":{"type":"string"},"markdown":{"type":"string"},"description":{"type":"string"},"parent_section_title":{"type":"string"},"section_title":{"type":"string"}},"additionalProperties":false}},"required":["module","operation","entityType","after"],"additionalProperties":false}}},"required":["changes"],"additionalProperties":false}
        """)
    ];

    public async Task<AgentTurnResult> RespondAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var resolvedScope = await ResolveScopeAsync(request.Scope, cancellationToken);
        var messages = new List<ModelMessage>
        {
            new("system", "You are the Personal OS assistant. Use only configured local Gemma. For application-data questions in Planning or Tasks call search_app and classify the request into that section. Never search Knowledge through this tool; direct Knowledge search questions to the dedicated search in the Knowledge section. The only changes you may propose are creating exactly one new knowledge.document or tasks.task. Never change existing data or create other object types. For ambiguity about section, intent, object type, title, or required content, ask a concise clarification and create nothing. Tasks require a title; description is optional. A knowledge document requires title and markdown. Never invent required values. Proposals require user confirmation. Reply in the user's language."),
        };
        if (resolvedScope.Mode == "entity")
        {
            var entity = await ReadCurrentByTypeAsync(resolvedScope.EntityType!, resolvedScope.EntityId!.Value, cancellationToken);
            messages.Add(new ModelMessage("system", "Current focused entity data at version " + resolvedScope.EntityVersion + ": " + JsonSerializer.Serialize(entity, JsonOptions)));
        }
        messages.AddRange(request.RecentMessages.TakeLast(10));
        if (messages.Count == 1 || messages[^1].Role != "user" || messages[^1].Content != request.Prompt)
            messages.Add(new ModelMessage("user", request.Prompt));

        var allSources = new List<SearchSourceReference>();
        RoutedCompletion? lastRoute = null;
        for (var round = 0; round <= MaxToolRounds; round++)
        {
            lastRoute = await models.CompleteAsync(request.RequestedModel,
                new ModelCompletionRequest(messages, Tools), cancellationToken);
            var completion = lastRoute.Completion;
            if (completion.ToolCalls.Count == 0)
                {
                    if (round <= 3 && NeedsToolReminder(request.Prompt))
                    {
                        var hadTools = allSources.Count > 0;
                        messages.Add(new ModelMessage("user", hadTools
                            ? "(Instruction) The search results above are your only source. Answer now in the user's language, quoting the returned titles. If the user asked to create a new document or task, call propose_changes now."
                            : "(Instruction) For data questions call search_app. Propose only creation of one new knowledge document or task. If section, object, or required values are unclear, ask the user; do not invent values."));
                        continue;
                    }
                    return new AgentTurnResult(GroundLookupAnswer(request.Prompt, completion.Content ?? string.Empty, allSources), resolvedScope, lastRoute.RequestedModel,
                    lastRoute.ActualModel, ResolveRoute(request.RequestedRoute, lastRoute), lastRoute.FallbackReason, null, allSources.Distinct().ToArray());
                }

            if (round == MaxToolRounds)
                return new AgentTurnResult(completion.Content ?? "I could not safely complete the request. Please narrow it and try again.",
                    resolvedScope, lastRoute.RequestedModel, lastRoute.ActualModel, ResolveRoute(request.RequestedRoute, lastRoute), lastRoute.FallbackReason, null, allSources.Distinct().ToArray());

            messages.Add(new ModelMessage("assistant", completion.Content ?? string.Empty, completion.ToolCalls));
            foreach (var call in completion.ToolCalls)
            {
                if (call.Name == "propose_changes")
                {
                    try
                    {
                        var proposal = await PrepareProposalAsync(call.ArgumentsJson, request, cancellationToken);
                        return new AgentTurnResult(completion.Content ?? "I prepared a change proposal for your review.", resolvedScope,
                            lastRoute.RequestedModel, lastRoute.ActualModel, ResolveRoute(request.RequestedRoute, lastRoute), lastRoute.FallbackReason, proposal, allSources.Distinct().ToArray());
                    }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException or JsonException)
                    {
                        messages.Add(new ModelMessage("tool", "Proposal rejected: " + error.Message +
                            " No data was changed. Only propose creating one new knowledge document or task. If required information is unclear, ask the user for clarification.", ToolCallId: call.Id, Name: call.Name));
                        continue;
                    }
                }

                var output = call.Name switch
                {
                    "search_app" => await SearchAsync(call.ArgumentsJson, request with { Scope = resolvedScope }, allSources, cancellationToken),
                    _ => "Unknown tool."
                };
                messages.Add(new ModelMessage("tool", output, ToolCallId: call.Id, Name: call.Name));
            }
        }
        throw new InvalidOperationException("Agent tool loop exited unexpectedly.");
    }

    private async Task<string> SearchAsync(string arguments, AgentTurnRequest request, List<SearchSourceReference> sourceReferences, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(arguments);
        var root = json.RootElement;
        var query = RequiredString(root, "query");
        var exhaustive = root.TryGetProperty("exhaustive", out var exhaustiveValue) && exhaustiveValue.GetBoolean();
        var section = RequiredString(root, "section");
        if (section == "knowledge")
            return "Поиск по базе знаний выполняется отдельно в разделе «База знаний». Перейдите туда и используйте его поиск.";
        var kinds = section switch
        {
            "planning" => new List<string> { "planning.project", "planning.milestone", "planning.feature" },
            "tasks" => new List<string> { "tasks.task", "tasks.section" },
            _ => throw new InvalidDataException("Choose one app section before searching.")
        };
                var context = request.Scope.Mode.Equals("entity", StringComparison.OrdinalIgnoreCase) && request.Scope.EntityId is not null
            ? new SearchChatFilter(EntityId: request.Scope.EntityId, EntityType: request.Scope.EntityType)
            : null;
        var pages = new List<SearchResponse>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var result = await search.SearchAsync(new SearchRequest(query,
                exhaustive ? SearchCoverageMode.Exhaustive : SearchCoverageMode.Relevant,
                kinds, context, Cursor: cursor, PageSize: exhaustive ? 100 : 20), cancellationToken);
            pages.Add(result);
            if (result.IsComplete || string.IsNullOrWhiteSpace(result.NextCursor)) break;
            cursor = result.NextCursor;
        }
        var hits = pages.SelectMany(x => x.Hits).Select(x => x.Source)
            .DistinctBy(x => (x.Kind, x.Id, x.Version)).ToArray();
        var isComplete = pages.Count > 0 && pages[^1].IsComplete;
        var coverageNote = string.Join(" ", pages.Select(x => x.CoverageNote).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        var items = new List<object>();
        foreach (var source in hits)
        {
            sourceReferences.Add(source);
            var item = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["historical"] = source.IsChatHistory
            };
            if (!source.IsChatHistory) item["currentEntity"] = await ReadCurrentByTypeAsync(source.Kind, source.Id, cancellationToken);
            items.Add(item);
        }
        return JsonSerializer.Serialize(new { isComplete, coverageNote, hits = items }, JsonOptions);
    }

    private async Task<string> ReadCurrentAsync(string arguments, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(arguments);
        var kind = RequiredString(json.RootElement, "entityType");
        var id = Guid.Parse(RequiredString(json.RootElement, "entityId"));
        var state = await ReadCurrentByTypeAsync(kind, id, cancellationToken);
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    private async Task<object?> ReadCurrentByTypeAsync(string kind, Guid id, CancellationToken cancellationToken) => kind switch
    {
        "knowledge.document" => await knowledgeAgent.ReadAsync(KnowledgeNodeKind.Document, id, cancellationToken),
        "knowledge.section" => await knowledgeAgent.ReadAsync(KnowledgeNodeKind.Section, id, cancellationToken),
        "planning.project" => await planning.ReadAsync(PlanningEntityKind.Project, id, cancellationToken),
        "planning.milestone" => await planning.ReadAsync(PlanningEntityKind.Milestone, id, cancellationToken),
        "planning.feature" => await planning.ReadAsync(PlanningEntityKind.Feature, id, cancellationToken),
        "tasks.task" => await tasks.ReadAsync(TaskEntityKind.Task, id, cancellationToken),
        "tasks.section" => await tasks.ReadAsync(TaskEntityKind.Section, id, cancellationToken),
        "chat.turn" => null,
        _ => throw new ArgumentException($"Unsupported entity type '{kind}'.", nameof(kind))
    };

    private async Task<ChangeProposal> PrepareProposalAsync(string arguments, AgentTurnRequest request, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(arguments);
        if (!json.RootElement.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Proposal changes must be an array.");
        if (changes.GetArrayLength() != 1)
            throw new InvalidDataException("Create one object at a time; ask for clarification if the request is ambiguous.");
        var prepared = new List<ProposedChange>();
        foreach (var item in changes.EnumerateArray())
        {
            var module = Enum.Parse<ChangeModule>(RequiredString(item, "module"), ignoreCase: true);
            var operation = Enum.Parse<ChangeOperation>(RequiredString(item, "operation"), ignoreCase: true);
            var entityType = NormalizeEntityType(module, RequiredString(item, "entityType"));
            if (operation != ChangeOperation.Create || entityType is not ("knowledge.document" or "tasks.task"))
                throw new InvalidDataException("Only creation of one new knowledge document or task is allowed. Ask for clarification if needed.");
            var afterElement = item.GetProperty("after");
            var idElement = OptionalString(item, "entityId");
            if (idElement is null && operation != ChangeOperation.Create)
                throw new InvalidDataException("entityId is required for " + operation + ". Use the exact ID of the existing object.");
            var id = idElement is null ? Guid.NewGuid() : Guid.Parse(idElement);
            var expectedVersion = item.TryGetProperty("expectedVersion", out var versionElement) ? versionElement.GetInt64() : (long?)null;
            ValidateChangeShape(module, operation, entityType, expectedVersion);
            var resolved = await ResolveParentAsync(entityType, operation, afterElement, cancellationToken);
            var afterString = BuildPayload(entityType, operation, afterElement, resolved);
            using var afterJson = JsonDocument.Parse(afterString);
            var payloadElement = afterJson.RootElement;
            ValidatePayload(entityType, operation, payloadElement);
            var current = operation == ChangeOperation.Create ? null : await ReadCurrentByTypeAsync(entityType, id, cancellationToken);
            var displayName = DeriveDisplayName(entityType, operation, current, payloadElement, id);
            var preview = DerivePreview(entityType, operation, payloadElement);
            var currentVersion = current switch
            {
                KnowledgeDocumentState document => document.Version,
                KnowledgeNodeState node => node.Version,
                PlanningEntityState entity => entity.Version,
                TaskEntityState task => task.Version,
                _ => (long?)null
            };
            if (operation != ChangeOperation.Create && (currentVersion is null || expectedVersion != currentVersion))
                throw new InvalidOperationException(displayName + " changed or no longer exists; read it again and prepare a new preview.");
            prepared.Add(ProposedChange.Create(operation,
                new ChangeTarget(module, entityType, id, expectedVersion), displayName,
                current is null ? null : JsonSerializer.Serialize(current, JsonOptions), afterString, preview));
        }
        return ChangeProposal.Prepare(request.ConversationId, request.TurnId, Guid.NewGuid().ToString("N"), prepared);
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : null;

    private async Task<Guid?> FindByTitleAsync(IReadOnlyList<string> kinds, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var wanted = title.Trim().ToLowerInvariant();
        string? cursor = null;
        for (var page = 0; page < 4; page++)
        {
            var result = await search.SearchAsync(new SearchRequest(title.Trim(), SearchCoverageMode.Exhaustive, kinds, null, Cursor: cursor, PageSize: 50), cancellationToken);
            foreach (var hit in result.Hits)
            {
                var source = hit.Source;
                if (kinds.Contains(source.Kind) && source.Title is not null && source.Title.Trim().ToLowerInvariant() == wanted)
                    return source.Id;
            }
            if (result.IsComplete || string.IsNullOrWhiteSpace(result.NextCursor)) break;
            cursor = result.NextCursor;
        }
        return null;
    }

    private async Task<Dictionary<string, object?>> ResolveParentAsync(string entityType, ChangeOperation operation, JsonElement after, CancellationToken cancellationToken)
    {
        var extra = new Dictionary<string, object?>();
        if (operation != ChangeOperation.Create) return extra;
        if (entityType == "knowledge.document" || entityType == "knowledge.section")
        {
            var parentTitle = TitleValue(after, "parent_section_title");
            if (parentTitle is not null)
            {
                var parentId = await FindByTitleAsync(["knowledge.section"], parentTitle, cancellationToken);
                if (parentId is null) throw new InvalidDataException("Section '" + parentTitle + "' was not found. Create it first or name it exactly.");
                extra["parentSectionId"] = parentId;
            }
        }
        else if (entityType == "planning.milestone" || entityType == "planning.feature")
        {
            var projectTitle = TitleValue(after, "project_title");
            var rawProjectId = TitleValue(after, "projectId");
            Guid? projectId = rawProjectId is null ? null : Guid.Parse(rawProjectId);
            if (projectTitle is not null)
            {
                projectId = await FindByTitleAsync(["planning.project"], projectTitle, cancellationToken);
                if (projectId is null) throw new InvalidDataException("Project '" + projectTitle + "' was not found. Create it first or name it exactly.");
            }
            if (projectId is null)
                throw new InvalidDataException("A milestone or a feature requires a project; specify after.project_title (or after.projectId).");
            extra["projectId"] = projectId;
            if (entityType == "planning.feature")
            {
                var milestoneTitle = TitleValue(after, "milestone_title");
                var rawMilestoneId = TitleValue(after, "milestoneId");
                Guid? milestoneId = rawMilestoneId is null ? null : Guid.Parse(rawMilestoneId);
                if (milestoneTitle is not null)
                {
                    milestoneId = await FindByTitleAsync(["planning.milestone"], milestoneTitle, cancellationToken);
                    if (milestoneId is null) throw new InvalidDataException("Milestone '" + milestoneTitle + "' was not found. Create it first or name it exactly.");
                    extra["milestoneId"] = milestoneId;
                }
                if (milestoneId is null)
                    throw new InvalidDataException("A feature requires a milestone; specify after.milestone_title (or after.milestoneId).");
                extra["milestoneId"] = milestoneId;
            }
        }
        else if (entityType == "tasks.task")
        {
            var sectionTitle = TitleValue(after, "section_title");
            if (sectionTitle is not null)
            {
                var sectionId = await FindByTitleAsync(["tasks.section"], sectionTitle, cancellationToken);
                if (sectionId is null) throw new InvalidDataException("Task section '" + sectionTitle + "' was not found. Create it first or name it exactly.");
                extra["sectionId"] = sectionId;
            }
            var projectTitle = TitleValue(after, "project_title");
            var featureTitle = TitleValue(after, "feature_title");
            if (projectTitle is not null && featureTitle is not null)
            {
                var projectId = await FindByTitleAsync(["planning.project"], projectTitle, cancellationToken);
                if (projectId is null) throw new InvalidDataException("Project '" + projectTitle + "' was not found. Create it first or name it exactly.");
                var featureId = await FindByTitleAsync(["planning.feature"], featureTitle, cancellationToken);
                if (featureId is null) throw new InvalidDataException("Feature '" + featureTitle + "' was not found. Create it first or name it exactly.");
                extra["planning"] = new Dictionary<string, object?> { ["projectId"] = projectId, ["milestoneId"] = null, ["featureId"] = featureId };
            }
        }
        return extra;
    }

    private static string? TitleValue(JsonElement after, string name) =>
        after.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : null;

    private static string BuildPayload(string entityType, ChangeOperation operation, JsonElement after, Dictionary<string, object?> resolved)
    {
        var payload = new Dictionary<string, object?>();
        var copy = (string name) =>
        {
            var value = OptionalString(after, name);
            if (value is not null) payload[name] = value;
        };
        if (entityType == "knowledge.document" || entityType == "knowledge.section")
        {
            copy("title");
            copy("markdown");
            if (entityType == "knowledge.document" && !payload.ContainsKey("markdown"))
                payload["markdown"] = "";
        }
        else if (entityType == "planning.project" || entityType == "planning.milestone" || entityType == "planning.feature")
        {
            copy("title");
            copy("description");
            var status = OptionalString(after, "feature_status");
            if (status is not null && entityType == "planning.feature") payload["featureStatus"] = status;
        }
        else if (entityType == "tasks.task")
        {
            copy("title");
            copy("description");
            var placement = OptionalString(after, "placement");
            var workStatus = OptionalString(after, "work_status");
            if (placement is not null) payload["placement"] = placement;
            if (workStatus is not null) payload["workStatus"] = workStatus;
        }
        else if (entityType == "tasks.section")
        {
            copy("title");
            var bucket = OptionalString(after, "placement");
            if (bucket is not null) payload["bucket"] = bucket;
        }
        foreach (var entry in resolved)
            payload[entry.Key] = entry.Value;
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"'{name}' is required.");

    private static string NormalizeEntityType(ChangeModule module, string value)
    {
        var type = value.Trim().ToLowerInvariant();
        if (type.Contains('.')) return type;
        return module switch
        {
            ChangeModule.Knowledge when type is "document" or "section" => "knowledge." + type,
            ChangeModule.Planning when type is "project" or "milestone" or "feature" => "planning." + type,
            ChangeModule.Tasks when type is "task" or "section" => "tasks." + type,
            _ => type
        };
    }

    private static bool NeedsToolReminder(string prompt)
    {
        var text = prompt.ToLowerInvariant();
        var operationWords = new[] { "создай", "создать", "добавь", "добавить", "измени", "изменить", "переимену", "переименовать",
            "перенес", "перемести", "перенести", "удали", "удалить", "архив", "отметь", "постав", "статус", "в сегодня", "в бэклог",
            "верни в план", "create", "add ", "rename", "move", "delete", "archive", "update", "status" };
        var dataWords = new[] { "у меня", "мои", "моя", "в базе", "база знаний", "в знаниях", "в задач", "документ", "раздел", "проект",
            "план", "заметк", "что ", "какие", "сколько", "найди", "найти", "где", "напомни", "перечисл", "список", "есть ли", "сводк",
            "обобщи", "про ", "по ", "my ", "what ", "find ", "list ", "search" };
        return operationWords.Any(word => text.Contains(word)) || (dataWords.Any(word => text.Contains(word)) && text.Length > 8);
    }

    private static string GroundLookupAnswer(string prompt, string modelAnswer, IReadOnlyList<SearchSourceReference> sources)
    {
        if (sources.Count == 0) return modelAnswer;
        var question = prompt.ToLowerInvariant();
        if (!(question.Contains("где") || question.Contains("найди") || question.Contains("перечисли") ||
              question.Contains("в каких") || question.Contains("упоминается") || question.Contains("find ") ||
              question.Contains("list "))) return modelAnswer;
        var current = sources.Where(source => !source.IsChatHistory)
            .DistinctBy(source => (source.Kind, source.Id, source.Version));
        if (question.Contains("документ")) current = current.Where(source => source.Kind == "knowledge.document");
        var items = current.ToArray();
        if (items.Length == 0) return modelAnswer;
        return "Найденные записи в текущих данных:\n" + string.Join("\n", items.Select((source, index) =>
            $"{index + 1}. {source.Title} — {source.Snippet}"));
    }

    private async Task<AgentScope> ResolveScopeAsync(AgentScope scope, CancellationToken cancellationToken)
    {
        if (!scope.Mode.Equals("entity", StringComparison.OrdinalIgnoreCase)) return new AgentScope("general", null, null, null);
        if (scope.EntityId is null || string.IsNullOrWhiteSpace(scope.EntityType))
            throw new ArgumentException("Entity scope requires an entity type and ID.", nameof(scope));
        var entity = await ReadCurrentByTypeAsync(scope.EntityType, scope.EntityId.Value, cancellationToken)
            ?? throw new KeyNotFoundException($"Focused entity {scope.EntityType}/{scope.EntityId} no longer exists.");
        var version = entity switch
        {
            KnowledgeNodeState node => node.Version,
            PlanningEntityState planningEntity => planningEntity.Version,
            TaskEntityState task => task.Version,
            _ => throw new InvalidOperationException("Focused entity has no version.")
        };
        return new AgentScope("entity", scope.EntityType, scope.EntityId, version);
    }

    private static void ValidateChangeShape(ChangeModule module, ChangeOperation operation, string entityType, long? expectedVersion)
    {
        var expectedModule = entityType switch
        {
            "knowledge.document" or "knowledge.section" => ChangeModule.Knowledge,
            "planning.project" or "planning.milestone" or "planning.feature" => ChangeModule.Planning,
            "tasks.task" or "tasks.section" => ChangeModule.Tasks,
            _ => throw new InvalidDataException($"Unsupported change entity '{entityType}'.")
        };
        if (module != expectedModule) throw new InvalidDataException("The change module does not match its entity type.");
        var supported = expectedModule switch
        {
            ChangeModule.Knowledge => operation is ChangeOperation.Create or ChangeOperation.Update or ChangeOperation.Move or ChangeOperation.Archive or ChangeOperation.Restore or ChangeOperation.Delete or ChangeOperation.Reorder,
            ChangeModule.Planning => operation is ChangeOperation.Create or ChangeOperation.Update or ChangeOperation.Archive or ChangeOperation.Restore or ChangeOperation.Delete or ChangeOperation.SetFeatureStatus or ChangeOperation.Reorder,
            ChangeModule.Tasks => operation is ChangeOperation.Create or ChangeOperation.Update or ChangeOperation.Move or ChangeOperation.Archive or ChangeOperation.Restore or ChangeOperation.Delete or ChangeOperation.SetWorkStatus or ChangeOperation.Reorder,
            _ => false
        };
        if (!supported) throw new InvalidDataException($"Operation {operation} is not supported for {entityType}.");
        if (operation != ChangeOperation.Create && expectedVersion is null)
            throw new InvalidDataException("Every existing-object change requires an expected version.");
        if (operation == ChangeOperation.SetFeatureStatus && entityType != "planning.feature")
            throw new InvalidDataException("Feature status can only be changed on a feature.");
        if (operation == ChangeOperation.SetWorkStatus && entityType != "tasks.task")
            throw new InvalidDataException("Work status can only be changed on a task.");
    }

    private static void ValidatePayload(string entityType, ChangeOperation operation, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Change values must be an object.");
        var allowed = (entityType, operation) switch
        {
            ("knowledge.document", ChangeOperation.Create) => new[] { "title", "markdown", "parentSectionId" },
            ("knowledge.document", ChangeOperation.Update) => new[] { "title", "markdown" },
            ("knowledge.document", ChangeOperation.Move) => new[] { "parentSectionId" },
            ("knowledge.document", ChangeOperation.Reorder) => new[] { "order" },
            ("knowledge.section", ChangeOperation.Create) => new[] { "title", "parentSectionId" },
            ("knowledge.section", ChangeOperation.Update) => new[] { "title" },
            ("knowledge.section", ChangeOperation.Move) => new[] { "parentSectionId" },
            ("knowledge.section", ChangeOperation.Reorder) => new[] { "order" },
            ("planning.project", ChangeOperation.Create or ChangeOperation.Update) => new[] { "title", "description" },
            ("planning.project", ChangeOperation.Reorder) => new[] { "order" },
            ("planning.milestone", ChangeOperation.Create) => new[] { "title", "description", "projectId", "expectedParentVersion" },
            ("planning.milestone", ChangeOperation.Update) => new[] { "title", "description" },
            ("planning.milestone", ChangeOperation.Reorder) => new[] { "order" },
            ("planning.feature", ChangeOperation.Create) => new[] { "title", "description", "projectId", "milestoneId", "expectedParentVersion" },
            ("planning.feature", ChangeOperation.Update) => new[] { "title", "description" },
            ("planning.feature", ChangeOperation.SetFeatureStatus) => new[] { "featureStatus" },
            ("planning.feature", ChangeOperation.Reorder) => new[] { "order" },
            ("tasks.task", ChangeOperation.Create) => new[] { "title", "description", "planning", "placement", "workStatus", "sectionId", "bucket" },
            ("tasks.task", ChangeOperation.Update) => new[] { "title", "description" },
            ("tasks.task", ChangeOperation.Move) => new[] { "planning", "placement", "sectionId", "bucket" },
            ("tasks.task", ChangeOperation.SetWorkStatus) => new[] { "workStatus" },
            ("tasks.task", ChangeOperation.Reorder) => new[] { "order" },
            ("tasks.section", ChangeOperation.Create or ChangeOperation.Update) => new[] { "title", "bucket" },
            ("tasks.section", ChangeOperation.Reorder) => new[] { "order" },
            _ => Array.Empty<string>()
        };
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name)) throw new InvalidDataException($"Unexpected '{property.Name}' value for {operation} {entityType}.");
            ValidatePayloadValue(property.Name, property.Value);
        }
        if (operation is ChangeOperation.Create or ChangeOperation.Update &&
            (!payload.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString())))
            throw new InvalidDataException("Create and update proposals require a non-empty title.");
        if (entityType == "knowledge.document" && operation is ChangeOperation.Create or ChangeOperation.Update &&
            !payload.TryGetProperty("markdown", out _))
            throw new InvalidDataException("Document proposals require markdown, including an empty string when clearing it.");
        if (operation == ChangeOperation.Reorder && !payload.TryGetProperty("order", out _))
            throw new InvalidDataException("Reorder proposals require the exact versioned order.");
        if (operation == ChangeOperation.SetFeatureStatus && !payload.TryGetProperty("featureStatus", out _))
            throw new InvalidDataException("Feature status proposals require featureStatus.");
        if (operation == ChangeOperation.SetWorkStatus && !payload.TryGetProperty("workStatus", out _))
            throw new InvalidDataException("Work status proposals require workStatus.");
    }

    private static void ValidatePayloadValue(string name, JsonElement value)
    {
        if (name == "order")
        {
            if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Order must be an array.");
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("id" or "expectedVersion")) ||
                    !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _) ||
                    !item.TryGetProperty("expectedVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out _))
                    throw new InvalidDataException("Every reordered object requires an exact ID and expected version.");
            }
            return;
        }
        if (name is "parentSectionId" or "projectId" or "milestoneId" or "sectionId")
        {
            if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out _)))
                throw new InvalidDataException($"'{name}' must be a GUID or null.");
            return;
        }
        if (name == "expectedParentVersion")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _)) throw new InvalidDataException("expectedParentVersion must be an integer.");
            return;
        }
        if (name == "planning")
        {
            if (value.ValueKind == JsonValueKind.Null) return;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("projectId" or "milestoneId" or "featureId")))
                throw new InvalidDataException("Planning link has an unsupported shape.");
            foreach (var property in value.EnumerateObject())
                if (property.Value.ValueKind != JsonValueKind.Null && (property.Value.ValueKind != JsonValueKind.String || !Guid.TryParse(property.Value.GetString(), out _)))
                    throw new InvalidDataException($"Planning link '{property.Name}' must be a GUID or null.");
            return;
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidDataException($"'{name}' must be a string or null.");
    }

    private static string DeriveDisplayName(string entityType, ChangeOperation operation, object? current, JsonElement after, Guid id)
    {
        var title = operation == ChangeOperation.Create && after.TryGetProperty("title", out var proposedTitle)
            ? proposedTitle.GetString()
            : current switch
            {
                KnowledgeNodeState node => node.Title,
                KnowledgeDocumentState document => document.Title,
                PlanningEntityState planningEntity => planningEntity.Title,
                TaskEntityState task => task.Title,
                _ => null
            };
        var kind = entityType switch
        {
            "knowledge.document" => "Документ знаний",
            "knowledge.section" => "Раздел знаний",
            "planning.project" => "Проект",
            "planning.milestone" => "Этап",
            "planning.feature" => "Функция",
            "tasks.task" => "Задача",
            "tasks.section" => "Раздел задач",
            _ => entityType
        };
        return string.IsNullOrWhiteSpace(title) ? $"{kind} · {id:D}" : $"{kind} · {title.Trim()}";
    }

    private static string DerivePreview(string entityType, ChangeOperation operation, JsonElement after)
    {
        var fields = after.EnumerateObject().Select(property => property.Name switch
        {
            "parentSectionId" => "родительский раздел",
            "projectId" => "проект",
            "milestoneId" => "этап",
            "featureStatus" => "статус функции",
            "workStatus" => "статус задачи",
            "expectedParentVersion" => "версия родителя",
            "sectionId" => "раздел",
            "planning" => "связь с планом",
            "placement" => "расположение",
            "order" => "порядок",
            "markdown" => "текст документа",
            "bucket" => "список задач",
            "description" => "описание",
            "title" => "название",
            _ => property.Name
        }).ToArray();
        var target = entityType switch
        {
            "knowledge.document" => "документ знаний",
            "knowledge.section" => "раздел знаний",
            "planning.project" => "проект",
            "planning.milestone" => "этап",
            "planning.feature" => "функцию",
            "tasks.task" => "задачу",
            "tasks.section" => "раздел задач",
            _ => entityType
        };
        var operationLabel = operation switch
        {
            ChangeOperation.Create => "Создать",
            ChangeOperation.Update => "Изменить",
            ChangeOperation.Move => "Переместить",
            ChangeOperation.Archive => "Архивировать",
            ChangeOperation.Restore => "Восстановить",
            ChangeOperation.Delete => "Удалить",
            ChangeOperation.SetWorkStatus => "Изменить статус задачи",
            ChangeOperation.SetFeatureStatus => "Изменить статус функции",
            ChangeOperation.Reorder => "Изменить порядок",
            _ => operation.ToString()
        };
        return fields.Length == 0
            ? $"{operationLabel} {target}."
            : $"{operationLabel} {target}: {string.Join(", ", fields)}. Точные значения показаны ниже.";
    }

    private static string FormatScope(AgentScope scope) => scope.Mode.Equals("entity", StringComparison.OrdinalIgnoreCase)
        ? $"entity {scope.EntityType} {scope.EntityId} at version {scope.EntityVersion}"
        : "general";

    private static ChatModelRoute ResolveRoute(ChatModelRoute requestedRoute, RoutedCompletion result) =>
        result.FallbackReason is not null ? ChatModelRoute.AutomaticFallback : requestedRoute;
}
