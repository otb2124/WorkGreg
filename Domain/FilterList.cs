namespace WorkGreg.Domain;

/// <summary>
/// Helpers for the filter lists on <see cref="SearchProfile"/> (Include, Exclude, Companies, Locations).
/// The database column stays a comma-separated TEXT value (see AppDb), so existing rows need no data migration.
/// </summary>
public static class FilterList
{
    public const string Separator = ", ";

    /// <summary>Splits a comma-separated text into a clean list. Null / empty gives an empty list.</summary>
    public static List<string> Parse(string? csv) => Clean(new[] { csv });

    /// <summary>
    /// Trims, drops blanks and removes duplicates (case-insensitive, first spelling wins).
    /// Items that contain commas are split, so a list can always round-trip through the comma-separated column.
    /// </summary>
    public static List<string> Clean(IEnumerable<string?>? items) =>
        (items ?? [])
            .SelectMany(i => (i ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Human-readable / stored form: "a, b, c". Empty string for an empty list.</summary>
    public static string Join(IEnumerable<string> items) => string.Join(Separator, items);
}
