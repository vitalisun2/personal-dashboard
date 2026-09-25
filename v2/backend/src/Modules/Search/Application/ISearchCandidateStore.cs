using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Application;

/// <summary>Search-owned port implemented by Infrastructure; returns all candidates allowed by the requested coverage mode.</summary>
public interface ISearchCandidateStore
{
    Task<SearchCandidateSet> FindCandidatesAsync(SearchCriteria criteria, SearchCoverageMode mode,
        CancellationToken cancellationToken = default);
}

public sealed record SearchCandidateSet(IReadOnlyList<SearchCandidate> Candidates, bool IsComplete = true,
    string? CoverageNote = null);
