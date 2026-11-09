using WorkGreg.Domain;

namespace WorkGreg.Filtering;

/// <summary>
/// A <see cref="SearchProfile"/> turned into criteria. Build it once per profile and reuse it for every job:
/// the same object decides whether a job is delivered and explains why.
/// </summary>
public sealed class ProfileCriteria
{
    private ProfileCriteria(KeywordCriterion? keyword, IReadOnlyList<IJobCriterion> filters)
    {
        Keyword = keyword;
        Filters = filters;
    }

    /// <summary>Null when the profile has no keyword.</summary>
    public KeywordCriterion? Keyword { get; }

    /// <summary>What a job must satisfy to be delivered (all of them). The keyword is not in here, see <see cref="KeywordCriterion"/>.</summary>
    public IReadOnlyList<IJobCriterion> Filters { get; }

    public static KeywordCriterion? KeywordOf(SearchProfile profile) =>
        WordMatcher.HasWords(profile.Keyword) ? new KeywordCriterion(profile.Keyword) : null;

    public static ProfileCriteria For(SearchProfile profile)
    {
        var filters = new List<IJobCriterion>();

        if (profile.Include.Count > 0) filters.Add(new IncludeCriterion(profile.Include));
        if (profile.Exclude.Count > 0) filters.Add(new ExcludeCriterion(profile.Exclude));
        if (profile.Companies.Count > 0) filters.Add(new CompanyCriterion(profile.Companies));
        if (profile.Locations.Count > 0) filters.Add(new LocationCriterion(profile.Locations));
        if (profile.RemoteOnly) filters.Add(new RemoteCriterion());
        if (profile.MinSalary is { } min) filters.Add(new MinSalaryCriterion(min));

        return new ProfileCriteria(KeywordOf(profile), filters);
    }

    public bool Matches(Job job) => Filters.All(f => f.Matches(job));

    /// <summary>Why the job matched: the keyword line, then one line per active filter. Call only for jobs that passed <see cref="Matches"/>.</summary>
    public List<string> Explain(Job job)
    {
        var lines = new List<string>();
        if (Keyword is not null) lines.Add(Keyword.Explain(job));
        lines.AddRange(Filters.Select(f => f.Explain(job)));
        return lines;
    }
}