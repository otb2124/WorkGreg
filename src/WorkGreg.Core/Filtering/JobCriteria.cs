using WorkGreg.Domain;

namespace WorkGreg.Filtering;

internal static class JobText
{
    /// <summary>Title, company, location and description as one normalized text (see <see cref="WordMatcher.Normalize"/>).</summary>
    public static string Haystack(Job job) =>
        WordMatcher.Normalize(job.Title, job.Company, job.Location, job.Description);
}

/// <summary>
/// The profile's search keyword. Applied when a fetched vacancy is stored, not when it is delivered, so changing the
/// keyword later doesn't retroactively drop vacancies that are already waiting. Because of that it is not part of
/// <see cref="ProfileCriteria.Filters"/>, but it still explains itself in the "Matched" list.
/// </summary>
public sealed class KeywordCriterion(string keyword) : IJobCriterion
{
    public string Keyword { get; } = keyword;

    public bool Matches(Job job) => Matches(job.Title, job.Description);

    /// <summary>Same rule for a vacancy that is not stored yet (title and description are all it looks at).</summary>
    public bool Matches(string? title, string? description) =>
        WordMatcher.Contains(WordMatcher.Normalize(title, description), Keyword);

    public string Explain(Job job) =>
        $"Keyword: {Keyword}" + (WordMatcher.Matches(job.Title, Keyword) ? " (in title)" : "");
}

/// <summary>Passes when at least one term occurs in the text this criterion looks at.</summary>
public abstract class AnyTermCriterion(IReadOnlyList<string> terms) : IJobCriterion
{
    protected IReadOnlyList<string> Terms { get; } = terms;

    protected abstract string Label { get; }

    /// <summary>The part of the job this criterion looks at, already normalized.</summary>
    protected abstract string TextOf(Job job);

    public bool Matches(Job job) => WordMatcher.ContainsAny(TextOf(job), Terms);

    public string Explain(Job job) =>
        $"{Label}: {FilterList.Join(WordMatcher.FindAll(TextOf(job), Terms))}";
}

/// <summary>Any of the terms in the title, company, location or description.</summary>
public sealed class IncludeCriterion(IReadOnlyList<string> terms) : AnyTermCriterion(terms)
{
    protected override string Label => "Include";
    protected override string TextOf(Job job) => JobText.Haystack(job);
}

/// <summary>Company only, not the whole text.</summary>
public sealed class CompanyCriterion(IReadOnlyList<string> terms) : AnyTermCriterion(terms)
{
    protected override string Label => "Company";
    protected override string TextOf(Job job) => WordMatcher.Normalize(job.Company);
}

/// <summary>Location only, not the whole text.</summary>
public sealed class LocationCriterion(IReadOnlyList<string> terms) : AnyTermCriterion(terms)
{
    protected override string Label => "Location";
    protected override string TextOf(Job job) => WordMatcher.Normalize(job.Location);
}

/// <summary>
/// None of the terms in the TITLE. Title only, so "work with senior engineers" in a description
/// doesn't kill a junior role.
/// </summary>
public sealed class ExcludeCriterion(IReadOnlyList<string> terms) : IJobCriterion
{
    public bool Matches(Job job) => !WordMatcher.ContainsAny(WordMatcher.Normalize(job.Title), terms);

    public string Explain(Job job) => $"Exclude: no title match ({FilterList.Join(terms)})";
}

/// <summary>The job looks remote. The marker list is used by the filter and by the "🌐 Remote" badge alike.</summary>
public sealed class RemoteCriterion : IJobCriterion
{
    // '*' = word prefix (see WordMatcher): "віддален*" covers "віддалена", "віддалений", "віддалено"...
    private static readonly string[] Markers =
        ["remote*", "віддален*", "удален*", "дистанц*", "work from home"];

    public static bool LooksRemote(Job job) => FindMarker(job) is not null;

    public bool Matches(Job job) => LooksRemote(job);

    public string Explain(Job job) =>
        FindMarker(job) is { } marker
            ? $"Remote only (found \"{marker.TrimEnd(WordMatcher.PrefixMarker)}\")"
            : "Remote only";

    private static string? FindMarker(Job job)
    {
        var text = JobText.Haystack(job);
        return Markers.FirstOrDefault(m => WordMatcher.Contains(text, m));
    }
}

/// <summary>
/// Salary at or above the minimum. A job that reports no salary does NOT meet the constraint,
/// otherwise sources that don't parse salaries would slip through.
/// </summary>
public sealed class MinSalaryCriterion(int minSalary) : IJobCriterion
{
    public bool Matches(Job job) => job.Salary is { } salary && salary >= minSalary;

    public string Explain(Job job) =>
        job.Salary is { } salary
            ? $"Salary: {salary:N0} (min {minSalary:N0})"
            : $"Salary: min {minSalary:N0}";
}