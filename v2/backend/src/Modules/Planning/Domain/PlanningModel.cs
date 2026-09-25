namespace PersonalDashboard.V2.Planning.Domain;

public enum FeatureStatus { Planned, Active, Done }

public sealed class Project
{
    private readonly List<Milestone> _milestones = [];
    private Project() { }
    public Project(string title, string? description = null, Guid? id = null, int position = 0) { Id = id ?? Guid.NewGuid(); Title = Required(title); Description = description?.Trim() ?? ""; Position = position; }
    public Guid Id { get; private set; }
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public long Version { get; private set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public int Position { get; private set; }
    public bool IsArchived { get; private set; }
    public IReadOnlyList<Milestone> Milestones => _milestones;
    public int ProgressPercent
    {
        get
        {
            var features = _milestones.SelectMany(x => x.Features).ToArray();
            return features.Length == 0 ? 0 : (int)Math.Round(100d * features.Count(x => x.Status == FeatureStatus.Done) / features.Length, MidpointRounding.AwayFromZero);
        }
    }
    public Milestone AddMilestone(string title, string? description = null, Guid? id = null) { var item = new Milestone(title, description, _milestones.Count, id); _milestones.Add(item); Touch(); return item; }
    public bool ReorderMilestones(IReadOnlyList<Guid> ids) { var changed = Reorder(_milestones, ids); if (changed) Touch(); return changed; }
    public void Rename(string title, string? description) { Title = Required(title); Description = description?.Trim() ?? ""; Touch(); MarkChildrenUpdated(); }
    public void Archive() { IsArchived = true; Touch(); MarkChildrenUpdated(); }
    public void Restore() { IsArchived = false; Touch(); MarkChildrenUpdated(); }
    public void SetPosition(int position) { if (Position == position) return; Position = position; Touch(); }
    public void RemoveMilestone(Guid id) { _milestones.Remove(_milestones.Single(x => x.Id == id)); Reindex(_milestones); Touch(); }
    internal static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Title is required.") : value.Trim();
    internal static bool Reorder<T>(List<T> items, IReadOnlyList<Guid> ids) where T : OrderedEntity { if (ids.Count != items.Count || ids.Distinct().Count() != ids.Count || !ids.ToHashSet().SetEquals(items.Select(x => x.Id))) throw new ArgumentException("Order must contain each current item exactly once."); var byId = items.ToDictionary(x => x.Id); var changed = false; items.Clear(); foreach (var (id, index) in ids.Select((id, index) => (id, index))) { var item = byId[id]; if (item.Position != index) { item.RecordPositionChange(); changed = true; } item.Position = index; items.Add(item); } return changed; }
    internal static void Reindex<T>(List<T> items) where T : OrderedEntity { for (var i = 0; i < items.Count; i++) { if (items[i].Position != i) items[i].RecordPositionChange(); items[i].Position = i; } }
    internal void Touch() { Version++; MarkUpdated(); }
    public void RecordChildChange() => Touch();
    private void MarkChildrenUpdated() { foreach (var milestone in _milestones) milestone.MarkPathChanged(); }
    private void MarkUpdated() => UpdatedAtUtc = NextTimestamp(UpdatedAtUtc);
    private static DateTimeOffset NextTimestamp(DateTimeOffset current) { var now = DateTimeOffset.UtcNow; return now > current ? now : current.AddTicks(1); }
}

public abstract class OrderedEntity
{
    protected OrderedEntity() { }
    protected OrderedEntity(string title, string? description, int position, Guid? id = null) { Id = id ?? Guid.NewGuid(); Title = Project.Required(title); Description = description?.Trim() ?? ""; Position = position; }
    public Guid Id { get; protected set; }
    public string Title { get; protected set; } = "";
    public string Description { get; protected set; } = "";
    public int Position { get; internal set; }
    public long Version { get; protected set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public virtual void Rename(string title, string? description) { Title = Project.Required(title); Description = description?.Trim() ?? ""; Touch(); }
    public void RecordChildChange() => Touch();
    internal virtual void MarkPathChanged() => MarkUpdated();
    internal void RecordPositionChange() { Version++; MarkUpdated(); }
    protected void Touch() { Version++; MarkUpdated(); }
    internal void MarkUpdated() { var now = DateTimeOffset.UtcNow; UpdatedAtUtc = now > UpdatedAtUtc ? now : UpdatedAtUtc.AddTicks(1); }
}

public sealed class Milestone : OrderedEntity
{
    private readonly List<Feature> _features = [];
    private Milestone() { }
    internal Milestone(string title, string? description, int position, Guid? id = null) : base(title, description, position, id) { }
    public IReadOnlyList<Feature> Features => _features;
    public int ProgressPercent => _features.Count == 0 ? 0 : (int)Math.Round(100d * _features.Count(x => x.Status == FeatureStatus.Done) / _features.Count, MidpointRounding.AwayFromZero);
    public Feature AddFeature(string title, string? description = null, Guid? id = null) { var feature = new Feature(title, description, _features.Count, id); _features.Add(feature); Touch(); return feature; }
    public bool ReorderFeatures(IReadOnlyList<Guid> ids) { var changed = Project.Reorder(_features, ids); if (changed) Touch(); return changed; }
    public void RemoveFeature(Guid id) { _features.Remove(_features.Single(x => x.Id == id)); Project.Reindex(_features); Touch(); }
    public override void Rename(string title, string? description) { base.Rename(title, description); foreach (var feature in _features) feature.MarkPathChanged(); }
    internal override void MarkPathChanged() { base.MarkPathChanged(); foreach (var feature in _features) feature.MarkPathChanged(); }
}

public sealed class Feature : OrderedEntity
{
    private Feature() { }
    internal Feature(string title, string? description, int position, Guid? id = null) : base(title, description, position, id) { }
    public FeatureStatus Status { get; private set; } = FeatureStatus.Planned;
    public void SetStatus(FeatureStatus status) { Status = status; Touch(); }
}
