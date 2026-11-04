namespace WorkGreg.Domain;

/// <summary>
/// A website we know how to search. The list is hardcoded on purpose:
/// users pick a source from it and give a keyword, they never paste raw URLs.
/// </summary>
/// <param name="Id">Stable id stored in Source.Provider and typed in commands, e.g. "dou".</param>
/// <param name="Name">Display name, e.g. "DOU.ua".</param>
/// <param name="Type">How it is fetched; the matching IJobFetcher must exist and be available.</param>
/// <param name="BuildUrl">Turns a (normalized) keyword into the URL the fetcher reads.</param>
/// <param name="KeywordExamples">Shown as hints when asking for a keyword.</param>
/// <param name="Html">Selectors for FetchType.Html / Browser providers.</param>
/// <param name="Api">Endpoint mapping for FetchType.Api providers.</param>
public sealed record SourceProvider(
    string Id,
    string Name,
    FetchType Type,
    Func<string, string> BuildUrl,
    IReadOnlyList<string> KeywordExamples,
    HtmlSourceConfig? Html = null,
    ApiSourceConfig? Api = null);

public static class SourceCatalog
{
    public const int MaxKeywordLength = 80;

    /// <summary>To support a new website: add one entry here (and a fetcher, if its FetchType is new).</summary>
    public static readonly IReadOnlyList<SourceProvider> All =
    [
        new SourceProvider(
            Id: "jobicy",
            Name: "Jobicy",
            Type: FetchType.Feed,
            BuildUrl: k => $"https://jobicy.com/?feed=job_feed&search_keywords={Uri.EscapeDataString(k)}",
            KeywordExamples: ["developer", "angular", "dotnet"]),

        // Example of a feed WITHOUT a search param (verify the URL first):
        new SourceProvider(
            Id: "wwr",
            Name: "We Work Remotely",
            Type: FetchType.Feed,
            BuildUrl: _ => "https://weworkremotely.com/remote-jobs.rss",
            KeywordExamples: ["developer", "angular"]),

        // JSON API. Remotive documents ?search=, but it has been reported as ignored, so we don't depend on it:
        // the keyword is enforced locally (SourceCatalog.MatchesKeyword), every keyword uses the same URL,
        // and PollRunner fetches that URL once per cycle for all searches.
        new SourceProvider(
            Id: "remotive",
            Name: "Remotive",
            Type: FetchType.Api,
            BuildUrl: _ => "https://remotive.com/api/remote-jobs",
            KeywordExamples: ["full stack", "angular", ".net"],
            Api: new ApiSourceConfig
            {
                ItemsPath = "jobs",
                FieldMap =
                {
                    ["id"] = "id",
                    ["title"] = "title",
                    ["url"] = "url",
                    ["company"] = "company_name",
                    ["location"] = "candidate_required_location",
                    ["description"] = "description",
                    ["posted"] = "publication_date",
                    ["employment"] = "job_type"
                }
            }),
    ];

    public static SourceProvider? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Trims and collapses whitespace. Null when empty or longer than <see cref="MaxKeywordLength"/>.</summary>
    public static string? NormalizeKeyword(string? raw)
    {
        var keyword = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return keyword.Length is > 0 and <= MaxKeywordLength ? keyword : null;
    }

    public static string DisplayName(SourceProvider provider, string keyword) => $"{provider.Name} · {keyword}";

    /// <summary>Whole-word, punctuation-insensitive match: "full stack" matches "Full-Stack" and "Fullstack"; ".NET" doesn't match "network".</summary>
    public static bool MatchesKeyword(string keyword, params string?[] texts)
    {
        var kw = NormalizeForMatch(keyword);
        if (kw.Length == 0) return true;

        var needles = new List<string> { $" {kw} " };
        if (kw.Contains(' ')) needles.Add($" {kw.Replace(" ", "")} ");   // "full stack" -> "fullstack"

        var hay = $" {NormalizeForMatch(string.Join(' ', texts.Where(t => t is not null)))} ";
        return needles.Any(n => hay.Contains(n, StringComparison.Ordinal));
    }

    private static string NormalizeForMatch(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var lastSpace = true;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }
}