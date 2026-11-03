using WorkGreg.Domain;

namespace WorkGreg.Filtering;

public static class FilterEngine
{
    private static readonly string[] RemoteMarkers =
        ["remote", "віддален", "удален", "дистанц", "work from home"];

    public static bool Matches(Job job, UserProfile profile)
    {
        // Exclude: title only, so "work with senior engineers" in a description doesn't kill a junior role.
        if (Split(profile.Exclude).Any(k => Has(job.Title, k)))
            return false;

        var haystack = $"{job.Title} {job.Company} {job.Location} {job.Description}";

        var include = Split(profile.Include);
        if (include.Count > 0 && !include.Any(k => Has(haystack, k)))
            return false;

        // Company filter: match against job.Company specifically, not the whole haystack.
        var companies = Split(profile.Companies);
        if (companies.Count > 0 && !companies.Any(c => Has(job.Company, c)))
            return false;

        // Location filter: same idea, match against job.Location only.
        var locations = Split(profile.Locations);
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

    public static List<string> Split(string? csv) =>
        (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Normalize(string? csv) => string.Join(", ", Split(csv));

    private static bool Has(string? text, string keyword) =>
        text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
}