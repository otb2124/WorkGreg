using WorkGreg.Domain;

namespace WorkGreg.Filtering;

public static class FilterEngine
{
    private static readonly string[] RemoteMarkers =
        ["remote", "віддален", "удален", "дистанц", "work from home"];

    public static bool Matches(Job job, SearchProfile profile)
    {
        // Exclude: title only, so "work with senior engineers" in a description doesn't kill a junior role.
        if (profile.Exclude.Any(k => Has(job.Title, k)))
            return false;

        var haystack = $"{job.Title} {job.Company} {job.Location} {job.Description}";

        var include = profile.Include;
        if (include.Count > 0 && !include.Any(k => Has(haystack, k)))
            return false;

        // Company filter: match against job.Company specifically, not the whole haystack.
        var companies = profile.Companies;
        if (companies.Count > 0 && !companies.Any(c => Has(job.Company, c)))
            return false;

        // Location filter: same idea, match against job.Location only.
        var locations = profile.Locations;
        if (locations.Count > 0 && !locations.Any(l => Has(job.Location, l)))
            return false;

        if (profile.RemoteOnly && !RemoteMarkers.Any(m => Has(haystack, m)))
            return false;

        // Salary: only constrain when the profile requires it AND the job actually reports a salary.
        // Treat "no salary reported" as "does not meet the constraint" — otherwise unparsed sources slip through.
        if (profile.MinSalary is { } min)
        {
            if (job.Salary is null || job.Salary < min)
                return false;
        }

        return true;
    }

    /// <summary>True if the job looks remote (same markers the RemoteOnly filter uses).</summary>
    public static bool IsRemote(Job job) =>
        RemoteMarkers.Any(m => Has($"{job.Title} {job.Company} {job.Location} {job.Description}", m));

    /// <summary>
    /// Human-readable list of the profile criteria this job satisfied (call only for jobs that passed
    /// <see cref="Matches"/>). Plain text; the caller HTML-encodes it.
    /// </summary>
    public static List<string> Explain(Job job, SearchProfile profile)
    {
        var lines = new List<string>();
        var haystack = $"{job.Title} {job.Company} {job.Location} {job.Description}";

        if (!string.IsNullOrWhiteSpace(profile.Keyword))
            lines.Add($"Keyword: {profile.Keyword}" + (Has(job.Title, profile.Keyword) ? " (in title)" : ""));

        var include = profile.Include.Where(k => Has(haystack, k)).ToList();
        if (include.Count > 0) lines.Add($"Include: {FilterList.Join(include)}");

        if (profile.Exclude.Count > 0) lines.Add($"Exclude: no title match ({FilterList.Join(profile.Exclude)})");

        var companies = profile.Companies.Where(c => Has(job.Company, c)).ToList();
        if (companies.Count > 0) lines.Add($"Company: {FilterList.Join(companies)}");

        var locations = profile.Locations.Where(l => Has(job.Location, l)).ToList();
        if (locations.Count > 0) lines.Add($"Location: {FilterList.Join(locations)}");

        if (profile.RemoteOnly)
        {
            var marker = RemoteMarkers.FirstOrDefault(mk => Has(haystack, mk));
            lines.Add(marker is null ? "Remote only" : $"Remote only (found \"{marker}\")");
        }

        if (profile.MinSalary is { } min && job.Salary is { } salary)
            lines.Add($"Salary: {salary:N0} (min {min:N0})");

        return lines;
    }

    private static bool Has(string? text, string keyword) =>
        text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
}
