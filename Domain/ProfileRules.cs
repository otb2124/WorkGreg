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

    public static Source NewSource(SearchProfile profile, SourceProvider provider) => new()
    {
        OwnerChatId = profile.OwnerChatId,
        SearchProfileId = profile.Id,
        Type = provider.Type,
        Provider = provider.Id,
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
