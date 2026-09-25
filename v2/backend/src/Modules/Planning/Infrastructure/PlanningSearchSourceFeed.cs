using System.Text;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Planning.Application;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Planning.Infrastructure.Persistence;

namespace PersonalDashboard.V2.Planning.Infrastructure;

public sealed class PlanningSearchSourceFeed(IPlanningRepository planning, PlatformDbContext db) : ISearchSourceFeed
{
    public async Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken ct = default)
    {
        (string Kind, Guid Id)? after = cursor is null ? null : DecodeCursor(cursor);
        var entries = new List<(string Kind, Guid Id, SearchSourceChange Change)>();
        foreach (var project in await planning.ListProjectsAsync(true, ct))
        {
            entries.Add(Current("planning.project", project.Id, project.Version, project.UpdatedAtUtc, project.Title, project.Description, project.Title, $"/planning/projects/{project.Id}"));
            foreach (var milestone in project.Milestones)
            {
                var path = $"{project.Title} / {milestone.Title}";
                entries.Add(Current("planning.milestone", milestone.Id, milestone.Version, milestone.UpdatedAtUtc, milestone.Title, milestone.Description, path, $"/planning/projects/{project.Id}/milestones/{milestone.Id}"));
                foreach (var feature in milestone.Features)
                    entries.Add(Current("planning.feature", feature.Id, feature.Version, feature.UpdatedAtUtc, feature.Title, $"{feature.Description}\nStatus: {feature.Status.ToString().ToLowerInvariant()}", $"{path} / {feature.Title}", $"/planning/projects/{project.Id}/milestones/{milestone.Id}/features/{feature.Id}"));
            }
        }
        var tombstones = await db.Set<PlanningTombstone>().AsNoTracking().ToListAsync(ct);
        entries.AddRange(tombstones.Select(x => (x.Type, x.Id, new SearchSourceChange(x.Type, x.Id, x.Version, true, null))));

        var ordered = entries.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal)
            .Where(x => after is null || Compare(x.Kind, x.Id, after.Value.Kind, after.Value.Id) > 0).ToArray();
        var size = Math.Clamp(pageSize, 1, 500);
        var page = ordered.Take(size).ToArray();
        var complete = ordered.Length <= size;
        var next = page.Length == 0 ? cursor : EncodeCursor(page[^1].Kind, page[^1].Id);
        return new(page.Select(x => x.Change).ToArray(), next, complete);
    }

    private static (string Kind, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var split = value.Split('|', 2);
            if (split.Length != 2 || !Guid.TryParse(split[1], out var id)) throw new FormatException();
            return (split[0], id);
        }
        catch (FormatException ex) { throw new ArgumentException("Invalid Planning search cursor.", nameof(cursor), ex); }
    }

    private static string EncodeCursor(string kind, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{kind}|{id:D}"));
    private static int Compare(string kind, Guid id, string afterKind, Guid afterId)
    {
        var typeOrder = string.Compare(kind, afterKind, StringComparison.Ordinal);
        return typeOrder != 0 ? typeOrder : string.Compare(id.ToString("D"), afterId.ToString("D"), StringComparison.Ordinal);
    }
    private static (string Kind, Guid Id, SearchSourceChange Change) Current(string kind, Guid id, long version, DateTimeOffset updatedAtUtc, string title, string body, string path, string url)
    {
        var source = new SearchIndexSource(kind, id, version, title, body, path, url, updatedAtUtc);
        return (kind, id, new SearchSourceChange(kind, id, version, false, source));
    }
}
