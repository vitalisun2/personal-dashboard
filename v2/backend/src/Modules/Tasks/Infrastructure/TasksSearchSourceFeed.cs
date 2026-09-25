using System.Text;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Tasks.Infrastructure.Persistence;

namespace PersonalDashboard.V2.Tasks.Infrastructure;

/// <summary>Current task state plus persisted task tombstones, ordered by stable entity key.</summary>
public sealed class TasksSearchSourceFeed(PlatformDbContext db, IPlanningPathReader paths) : ISearchSourceFeed
{
    public async Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken ct = default)
    {
        (string Kind, Guid Id)? after = cursor is null ? null : DecodeCursor(cursor);
        var tasks = await db.Set<TaskItem>().AsNoTracking().ToListAsync(ct);
        var activeIds = tasks.Select(x => x.Id).ToArray();
        var tombstones = await db.Set<TaskTombstone>().AsNoTracking()
            .Where(x => !activeIds.Contains(x.Id))
            .ToListAsync(ct);

        var entries = new List<(string Kind, Guid Id, SearchSourceChange Change)>(tasks.Count + tombstones.Count);
        foreach (var task in tasks)
        {
            PlanningPath? path = task.ProjectId is { } projectId
                ? await paths.ReadPathAsync(new PlanningLink(projectId, task.MilestoneId, task.FeatureId), ct)
                : null;
            var source = new SearchIndexSource(
                "tasks.task", task.Id, task.Version, task.Title, task.Description,
                path?.Path, $"/tasks/{task.Id}", task.UpdatedAtUtc);
            entries.Add((source.Kind, source.Id, new SearchSourceChange(source.Kind, source.Id, source.Version, false, source)));
        }
        entries.AddRange(tombstones.Select(x => ("tasks.task", x.Id, new SearchSourceChange("tasks.task", x.Id, x.Version, true, null))));

        var ordered = entries.OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal)
            .Where(x => after is null || Compare(x.Kind, x.Id, after.Value.Kind, after.Value.Id) > 0)
            .ToArray();
        var size = Math.Clamp(pageSize, 1, 500);
        var page = ordered.Take(size + 1).ToArray();
        var complete = page.Length <= size;
        if (!complete) page = page[..size];
        var next = page.Length == 0 ? cursor : EncodeCursor(page[^1].Kind, page[^1].Id);
        return new(page.Select(x => x.Change).ToArray(), next, complete);
    }

    private static (string Kind, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var split = value.Split('|', 2);
            if (split.Length != 2 || split[0] != "tasks.task" || !Guid.TryParse(split[1], out var id)) throw new FormatException();
            return (split[0], id);
        }
        catch (FormatException ex) { throw new ArgumentException("Invalid Tasks search cursor.", nameof(cursor), ex); }
    }

    private static string EncodeCursor(string kind, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{kind}|{id:D}"));
    private static int Compare(string kind, Guid id, string afterKind, Guid afterId)
    {
        var typeOrder = StringComparer.Ordinal.Compare(kind, afterKind);
        return typeOrder != 0 ? typeOrder : StringComparer.Ordinal.Compare(id.ToString("D"), afterId.ToString("D"));
    }
}
