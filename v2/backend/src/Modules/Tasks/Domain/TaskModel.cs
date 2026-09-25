namespace PersonalDashboard.V2.Tasks.Domain;

public enum TaskLocation { Planned, Backlog, Today, Archived }
public enum TaskWorkStatus { New, InProgress, Done }

public sealed class TaskItem
{
    private TaskItem() { }
    public TaskItem(string title, string? description = null, Guid? projectId = null, Guid? milestoneId = null, Guid? featureId = null, Guid? sectionId = null, Guid? id = null)
    {
        if ((projectId is null) != (milestoneId is null) || (milestoneId is null) != (featureId is null)) throw new ArgumentException("A planning link requires project, milestone and feature ids together.");
        Id = id ?? Guid.NewGuid(); Title = Required(title); Description = description?.Trim() ?? "";
        ProjectId = projectId; MilestoneId = milestoneId; FeatureId = featureId;
        Location = featureId is null ? TaskLocation.Backlog : TaskLocation.Planned;
        if (featureId is not null && sectionId is not null) throw new ArgumentException("Planning tasks use their derived project section.");
        SectionId = sectionId;
    }
    public Guid Id { get; private set; }
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public Guid? ProjectId { get; private set; }
    public Guid? MilestoneId { get; private set; }
    public Guid? FeatureId { get; private set; }
    public TaskLocation Location { get; private set; }
    public TaskWorkStatus WorkStatus { get; private set; } = TaskWorkStatus.New;
    public Guid? SectionId { get; private set; }
    public int Position { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public void Edit(string title, string? description) { Title = Required(title); Description = description?.Trim() ?? ""; Touch(); }
    public void Update(string? title, string? description, Guid? sectionId)
    {
        if (title is not null) Title = Required(title);
        if (description is not null) Description = description.Trim();
        if (sectionId is not null)
        {
            if (ProjectId is not null) throw new InvalidOperationException("Planning tasks use their derived project section.");
            SectionId = sectionId;
        }
        Touch();
    }
    public void SetSection(Guid sectionId) { if (ProjectId is not null) throw new InvalidOperationException("Planning tasks use their derived project section."); SectionId = sectionId; Touch(); }
    public void MoveToToday(Guid? sectionId) { if (Location != TaskLocation.Backlog) throw new InvalidOperationException("Move planned tasks to Backlog before selecting them for Today."); Location = TaskLocation.Today; SectionId = ProjectId is null ? sectionId : null; WorkStatus = TaskWorkStatus.New; Touch(); }
    public void MoveToBacklog(Guid? sectionId) { if (Location is not (TaskLocation.Today or TaskLocation.Planned)) throw new InvalidOperationException("Only Today or planned tasks can move to Backlog."); Location = TaskLocation.Backlog; SectionId = ProjectId is null ? sectionId : null; WorkStatus = TaskWorkStatus.New; Touch(); }
    public void ReturnToPlan() { if (Location != TaskLocation.Backlog || FeatureId is null) throw new InvalidOperationException("Only linked backlog tasks can return to planning."); Location = TaskLocation.Planned; SectionId = null; WorkStatus = TaskWorkStatus.New; Touch(); }
    public void SetWorkStatus(TaskWorkStatus status) { if (Location != TaskLocation.Today) throw new InvalidOperationException("Work status applies only to Today tasks."); WorkStatus = status; Touch(); }
    public void Archive() { Location = TaskLocation.Archived; Touch(); }
    public void Restore(Guid? sectionId) { if (Location != TaskLocation.Archived) throw new InvalidOperationException("Only archived tasks can be restored."); Location = TaskLocation.Backlog; SectionId = ProjectId is null ? sectionId : null; WorkStatus = TaskWorkStatus.New; Touch(); }
    internal static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    public void SetPosition(int position) { Position = position; Touch(); }
    public void RefreshPlanningProjection() => Touch();
    private void Touch() { Version++; UpdatedAtUtc = DateTimeOffset.UtcNow; }
}

public sealed class TaskSection
{
    private TaskSection() { }
    public TaskSection(string name, TaskLocation location, Guid? id = null, int position = 0) { if (location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new ArgumentException("Sections belong to Backlog or Today."); Id = id ?? Guid.NewGuid(); Name = TaskItem.Required(name); Location = location; Position = position; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public TaskLocation Location { get; private set; }
    public int Position { get; private set; }
    public long Version { get; private set; } = 1;
    public void Rename(string name) { Name = TaskItem.Required(name); Version++; }
    public void Reorder(int position) { Position = position; Version++; }
}
