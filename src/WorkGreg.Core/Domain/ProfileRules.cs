namespace WorkGreg.Domain;

/// <summary>Validation and shared mutations for <see cref="SearchProfile"/>, used by both the bot and the HTTP API.</summary>
public static class ProfileRules
{
    public const int MaxProfilesPerUser = 10;
    public const int MaxNameLength = 40;

    /// <summary>Trims and collapses whitespace. Null when empty or longer than <see cref="MaxNameLength"/>.</summary>
    public static string? NormalizeName(string? raw)
    {
        var name = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length is > 0 and <= MaxNameLength ? name : null;
    }

    /// <summary>Default profile name for a keyword (the keyword itself, shortened if needed).</summary>
    public static string NameFromKeyword(string keyword) =>
        keyword.Length <= MaxNameLength ? keyword : keyword[..MaxNameLength];

    /// <summary>
    /// The form a provider id is stored and compared in: trimmed, lower case. A catalog id is already lower case
    /// (<see cref="CatalogValidator"/> enforces it), so a known provider resolves to its catalog id; an id that is not in
    /// the catalog any more (a removed provider on an old row) is just trimmed and lower-cased.
    /// Use this on anything typed by a user before comparing it with <see cref="Source.Provider"/>.
    /// </summary>
    public static string NormalizeProviderId(string? raw)
    {
        var id = (raw ?? "").Trim();
        return (SourceCatalog.Find(id)?.Id ?? id).ToLowerInvariant();
    }

    public static Source NewSource(SearchProfile profile, SourceProvider provider) => new()
    {
        SearchProfileId = profile.Id,
        Type = provider.Type,

        // Lower case at write time, so the column can be compared with a plain "=" everywhere.
        Provider = provider.Id.ToLowerInvariant(),
        Url = provider.BuildUrl(profile.Keyword),
        Name = provider.Name
    };

    /// <summary>Sets the keyword and re-derives every source URL. Failures are reset because the search changed.</summary>
    public static void ApplyKeyword(SearchProfile profile, string keyword, IEnumerable<Source> sources)
    {
        profile.Keyword = keyword;
        foreach (var s in sources)
        {
            var provider = SourceCatalog.Find(s.Provider);
            if (provider is not null) s.Url = provider.BuildUrl(keyword);
            s.ConsecutiveFailures = 0;
            s.LastError = null;
        }
    }
}