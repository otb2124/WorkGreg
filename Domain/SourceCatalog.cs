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
/// <param name="Regions">Region ids (see <see cref="SourceCatalog.Regions"/>) this source is listed under. Null = "global".</param>
public sealed record SourceProvider(
    string Id,
    string Name,
    FetchType Type,
    Func<string, string> BuildUrl,
    IReadOnlyList<string> KeywordExamples,
    HtmlSourceConfig? Html = null,
    ApiSourceConfig? Api = null,
    IReadOnlyList<string>? Regions = null);

public sealed record SourceRegion(string Id, string Label);

public static class SourceCatalog
{
    public const int MaxKeywordLength = 80;

    /// <summary>Menu order. A region with no available sources is hidden by the bot.</summary>
    public static readonly IReadOnlyList<SourceRegion> Regions =
    [
        new("hr", "🇭🇷 Croatia"),
        new("us", "🇺🇸 USA"),
        new("de", "🇩🇪 Germany"),
        new("eu", "🇪🇺 Europe"),
        new("pl", "🇵🇱 Poland"),
        new("ua", "🇺🇦 Ukraine"),
        new("global", "🌍 Global / remote"),
    ];

    public static IReadOnlyList<string> RegionsOf(SourceProvider p) => p.Regions ?? new[] { "global" };

    /// <summary>To support a new website: add one entry here (and a fetcher, if its FetchType is new).</summary>
    public static readonly IReadOnlyList<SourceProvider> All =
    [
        // =====================================================================================
        //  GLOBAL / REMOTE BOARDS  (the 3 originals keep their ids)
        // =====================================================================================
        new SourceProvider(
            Id: "jobicy",
            Name: "Jobicy",
            Type: FetchType.Feed,
            BuildUrl: k => $"https://jobicy.com/?feed=job_feed&search_keywords={Uri.EscapeDataString(k)}",
            KeywordExamples: ["developer", "angular", "dotnet"]),

        new SourceProvider(
            Id: "wwr",
            Name: "We Work Remotely",
            Type: FetchType.Feed,
            BuildUrl: _ => "https://weworkremotely.com/remote-jobs.rss",
            KeywordExamples: ["developer", "angular"]),

        // Remotive documents ?search=, but it has been reported as ignored, so we don't depend on it:
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

        // Root of the response is an array; its first element is a legal notice without "position", which is skipped.
        new SourceProvider(
            Id: "remoteok",
            Name: "Remote OK",
            Type: FetchType.Api,
            BuildUrl: _ => "https://remoteok.com/api",
            KeywordExamples: ["developer", "angular", "dotnet"],
            Api: new ApiSourceConfig
            {
                ItemsPath = "",
                FieldMap =
                {
                    ["id"] = "id",
                    ["title"] = "position",
                    ["url"] = "url",
                    ["company"] = "company",
                    ["location"] = "location",
                    ["description"] = "description",
                    ["posted"] = "date"
                }
            }),

        new SourceProvider(
            Id: "himalayas",
            Name: "Himalayas",
            Type: FetchType.Api,
            BuildUrl: _ => "https://himalayas.app/jobs/api?limit=100",
            KeywordExamples: ["developer", "angular", "product"],
            Api: new ApiSourceConfig
            {
                ItemsPath = "jobs",
                FieldMap =
                {
                    ["id"] = "guid",
                    ["title"] = "title",
                    ["url"] = "applicationLink",
                    ["company"] = "companyName",
                    ["description"] = "description",
                    ["posted"] = "pubDate",
                    ["employment"] = "employmentType"
                }
            }),

        new SourceProvider(
            Id: "workingnomads",
            Name: "Working Nomads",
            Type: FetchType.Api,
            BuildUrl: _ => "https://www.workingnomads.com/api/exposed_jobs/",
            KeywordExamples: ["developer", "angular", "devops"],
            Api: new ApiSourceConfig
            {
                ItemsPath = "",
                FieldMap =
                {
                    ["title"] = "title",
                    ["url"] = "url",
                    ["company"] = "company_name",
                    ["location"] = "location",
                    ["description"] = "description",
                    ["posted"] = "pub_date"
                }
            }),

        // WordPress "WP Job Manager" feeds: same ?feed=job_feed&search_keywords= format that Jobicy uses.
        new SourceProvider(
            Id: "jobspresso",
            Name: "Jobspresso",
            Type: FetchType.Feed,
            BuildUrl: k => $"https://jobspresso.co/?feed=job_feed&search_keywords={Uri.EscapeDataString(k)}",
            KeywordExamples: ["developer", "angular", "support"]),

        // We Work Remotely, per category (the plain "wwr" source above is the all-in-one feed).
        Wwr("wwr-programming", "WWR · Programming", "remote-programming-jobs"),
        Wwr("wwr-fullstack", "WWR · Full-stack", "remote-full-stack-programming-jobs"),
        Wwr("wwr-frontend", "WWR · Front-end", "remote-front-end-programming-jobs"),
        Wwr("wwr-backend", "WWR · Back-end", "remote-back-end-programming-jobs"),
        Wwr("wwr-devops", "WWR · DevOps / Sysadmin", "remote-devops-sysadmin-jobs"),
        Wwr("wwr-design", "WWR · Design", "remote-design-jobs"),
        Wwr("wwr-product", "WWR · Product", "remote-product-jobs"),
        Wwr("wwr-support", "WWR · Customer support", "remote-customer-support-jobs"),
        Wwr("wwr-marketing", "WWR · Sales & marketing", "remote-sales-and-marketing-jobs"),
        Wwr("wwr-management", "WWR · Management & finance", "remote-management-and-finance-jobs"),

        // =====================================================================================
        //  USA
        // =====================================================================================
        // Y Combinator "Jobs" posts from Hacker News.
        new SourceProvider(
            Id: "hnjobs",
            Name: "Hacker News Jobs",
            Type: FetchType.Feed,
            BuildUrl: _ => "https://hnrss.org/jobs",
            KeywordExamples: ["engineer", "founding", "remote"],
            Regions: ["us"]),

        // The public endpoint returns one page (20 jobs); category narrows it to engineering.
        new SourceProvider(
            Id: "themuse",
            Name: "The Muse · Engineering",
            Type: FetchType.Api,
            BuildUrl: _ => "https://www.themuse.com/api/public/jobs?category=Software%20Engineering&page=1&descending=true",
            KeywordExamples: ["engineer", "frontend", "data"],
            Regions: ["us"],
            Api: new ApiSourceConfig
            {
                ItemsPath = "results",
                FieldMap =
                {
                    ["id"] = "id",
                    ["title"] = "name",
                    ["url"] = "refs.landing_page",
                    ["company"] = "company.name",
                    ["location"] = "locations.0.name",
                    ["description"] = "contents",
                    ["posted"] = "publication_date"
                }
            }),

        Jobicy("jobicy-usa", "Jobicy · USA", "usa", ["us"]),

        // Company careers boards (public, no key). Token = the name in the company's boards URL.
        Greenhouse("gitlab", "GitLab", ["us"]),
        Greenhouse("cloudflare", "Cloudflare", ["us"]),
        Greenhouse("datadog", "Datadog", ["us"]),
        Greenhouse("stripe", "Stripe", ["us"]),
        Greenhouse("figma", "Figma", ["us"]),
        Greenhouse("discord", "Discord", ["us"]),
        Greenhouse("mongodb", "MongoDB", ["us"]),
        Greenhouse("elastic", "Elastic", ["us"]),
        Greenhouse("databricks", "Databricks", ["us"]),
        Greenhouse("airbnb", "Airbnb", ["us"]),
        Greenhouse("coinbase", "Coinbase", ["us"]),
        Lever("palantir", "Palantir", ["us"]),
        Ashby("openai", "OpenAI", ["us"]),
        Ashby("notion", "Notion", ["us"]),
        Ashby("ramp", "Ramp", ["us"]),
        Ashby("linear", "Linear", ["us"]),
        Ashby("supabase", "Supabase", ["us"]),

        // =====================================================================================
        //  GERMANY
        // =====================================================================================
        // Germany's biggest job board (Bundesagentur für Arbeit). Public key, no signup.
        // Titles are mostly German ("Softwareentwickler"), so use German keywords, or "angular", "react", ".net" etc.
        // The API has no URL field, so the detail link is built from the reference number.
        BundesagenturJobs("ba", "Bundesagentur für Arbeit", ""),
        BundesagenturJobs("ba-homeoffice", "Bundesagentur · Homeoffice", "&arbeitszeit=ho"),

        new SourceProvider(
            Id: "arbeitnow",
            Name: "Arbeitnow",
            Type: FetchType.Api,
            BuildUrl: _ => "https://www.arbeitnow.com/api/job-board-api",
            KeywordExamples: ["developer", "angular", "devops"],
            Regions: ["de", "eu"],
            Api: ArbeitnowApi()),

        // Arbeitnow's documented visa_sponsorship=true filter.
        new SourceProvider(
            Id: "arbeitnow-visa",
            Name: "Arbeitnow · Visa sponsorship",
            Type: FetchType.Api,
            BuildUrl: _ => "https://www.arbeitnow.com/api/job-board-api?visa_sponsorship=true",
            KeywordExamples: ["developer", "engineer", "data"],
            Regions: ["de", "eu"],
            Api: ArbeitnowApi()),

        // Not test-fetched: if the feed turns out to mix in non-job posts, drop it.
        new SourceProvider(
            Id: "berlinstartupjobs",
            Name: "Berlin Startup Jobs",
            Type: FetchType.Feed,
            BuildUrl: _ => "https://berlinstartupjobs.com/feed/",
            KeywordExamples: ["engineer", "frontend", "python"],
            Regions: ["de"]),

        Jobicy("jobicy-germany", "Jobicy · Germany", "germany", ["de"]),
        Greenhouse("getyourguide", "GetYourGuide", ["de"]),
        Greenhouse("contentful", "Contentful", ["de"]),

        // =====================================================================================
        //  EUROPE
        // =====================================================================================
        Jobicy("jobicy-europe", "Jobicy · Europe", "europe", ["eu"]),

        Lever("spotify", "Spotify", ["eu"]),

        // =====================================================================================
        //  POLAND
        // =====================================================================================
        Jobicy("jobicy-poland", "Jobicy · Poland", "poland", ["pl"]),

        // =====================================================================================
        //  UKRAINE
        // =====================================================================================
        // DOU.ua vacancy RSS. The first try (/vacancies/feed/) returned 404; /vacancies/feeds/ is the path DOU links from its pages. Not test-fetched yet.
        new SourceProvider(
            Id: "dou",
            Name: "DOU.ua",
            Type: FetchType.Feed,
            BuildUrl: k => $"https://jobs.dou.ua/vacancies/feeds/?search={Uri.EscapeDataString(k)}",
            KeywordExamples: ["angular", ".net", "python"],
            Regions: ["ua"]),

        new SourceProvider(
            Id: "dou-remote",
            Name: "DOU.ua · Remote",
            Type: FetchType.Feed,
            BuildUrl: k => $"https://jobs.dou.ua/vacancies/feeds/?remote&search={Uri.EscapeDataString(k)}",
            KeywordExamples: ["angular", ".net", "python"],
            Regions: ["ua"]),

        // Djinni RSS has no free-text search, so the keyword is enforced locally. Not test-fetched.
        new SourceProvider(
            Id: "djinni",
            Name: "Djinni",
            Type: FetchType.Feed,
            BuildUrl: _ => "https://djinni.co/jobs/rss/",
            KeywordExamples: ["angular", ".net", "python"],
            Regions: ["ua"]),

        new SourceProvider(
            Id: "djinni-remote",
            Name: "Djinni · Remote",
            Type: FetchType.Feed,
            BuildUrl: _ => "https://djinni.co/jobs/rss/?employment=remote",
            KeywordExamples: ["angular", ".net", "python"],
            Regions: ["ua"]),

        // =====================================================================================
        //  CROATIA
        // =====================================================================================
        // MojPosao, Posao.hr and Burza rada (HZZ) have no RSS/JSON API: they need the Html fetcher (see notes).
        Greenhouse("microblink", "Microblink", ["hr"]),
    ];

    // ------------------------------------------------------------------ entry builders

    private static SourceProvider Wwr(string id, string name, string feedName) => new(
        Id: id,
        Name: name,
        Type: FetchType.Feed,
        BuildUrl: _ => $"https://weworkremotely.com/categories/{feedName}.rss",
        KeywordExamples: ["developer", "angular"]);

    /// <summary>Jobicy JSON API v2; geo is one of Jobicy's region names (usa, germany, poland, europe, ...).</summary>
    private static SourceProvider Jobicy(string id, string name, string geo, string[] regions) => new(
        Id: id,
        Name: name,
        Type: FetchType.Api,
        BuildUrl: _ => $"https://jobicy.com/api/v2/remote-jobs?count=100&geo={geo}",
        KeywordExamples: ["developer", "angular", "dotnet"],
        Regions: regions,
        Api: new ApiSourceConfig
        {
            ItemsPath = "jobs",
            FieldMap =
            {
                ["id"] = "id",
                ["title"] = "jobTitle",
                ["url"] = "url",
                ["company"] = "companyName",
                ["location"] = "jobGeo",
                ["description"] = "jobDescription",
                ["posted"] = "pubDate"
            }
        });

    private static ApiSourceConfig ArbeitnowApi() => new()
    {
        ItemsPath = "data",
        FieldMap =
        {
            ["id"] = "slug",
            ["title"] = "title",
            ["url"] = "url",
            ["company"] = "company_name",
            ["location"] = "location",
            ["description"] = "description",
            ["posted"] = "created_at"
        }
    };

    private static SourceProvider BundesagenturJobs(string id, string name, string extraQuery) => new(
        Id: id,
        Name: name,
        Type: FetchType.Api,
        // The site's own search term (was=) narrows the result; the keyword is still enforced locally afterwards.
        BuildUrl: k => "https://rest.arbeitsagentur.de/jobboerse/jobsuche-service/pc/v6/jobs" +
                       $"?angebotsart=1&size=100&veroeffentlichtseit=14&was={Uri.EscapeDataString(k)}{extraQuery}",
        KeywordExamples: ["softwareentwickler", "angular", "devops"],
        Regions: ["de"],
        Api: new ApiSourceConfig
        {
            Headers = { ["X-API-Key"] = "jobboerse-jobsuche" },
            ItemsPath = "stellenangebote",
            MissingItemsIsEmpty = true,   // the API leaves the array out when nothing matches
            UrlTemplate = "https://www.arbeitsagentur.de/jobsuche/jobdetail/{url}",
            FieldMap =
            {
                ["id"] = "refnr",
                ["title"] = "titel",
                ["url"] = "refnr",
                ["company"] = "arbeitgeber",
                ["location"] = "arbeitsort.ort",
                ["description"] = "beruf",
                ["posted"] = "aktuelleVeroeffentlichungsdatum"
            }
        });

    /// <summary>Greenhouse careers board (public API). token = the name in boards.greenhouse.io/{token}.</summary>
    private static SourceProvider Greenhouse(string token, string company, string[] regions) => new(
        Id: $"gh-{token}",
        Name: $"{company} (careers)",
        Type: FetchType.Api,
        BuildUrl: _ => $"https://boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true",
        KeywordExamples: ["engineer", "frontend", "data"],
        Regions: regions,
        Api: new ApiSourceConfig
        {
            ItemsPath = "jobs",
            Company = company,
            FieldMap =
            {
                ["id"] = "id",
                ["title"] = "title",
                ["url"] = "absolute_url",
                ["location"] = "location.name",
                ["description"] = "content",
                ["posted"] = "updated_at"
            }
        });

    /// <summary>Lever careers board (public API). token = the name in jobs.lever.co/{token}.</summary>
    private static SourceProvider Lever(string token, string company, string[] regions) => new(
        Id: $"lever-{token}",
        Name: $"{company} (careers)",
        Type: FetchType.Api,
        BuildUrl: _ => $"https://api.lever.co/v0/postings/{token}?mode=json",
        KeywordExamples: ["engineer", "frontend", "data"],
        Regions: regions,
        Api: new ApiSourceConfig
        {
            ItemsPath = "",
            Company = company,
            FieldMap =
            {
                ["id"] = "id",
                ["title"] = "text",
                ["url"] = "hostedUrl",
                ["location"] = "categories.location",
                ["description"] = "descriptionPlain",
                ["posted"] = "createdAt",
                ["employment"] = "categories.commitment"
            }
        });

    /// <summary>Ashby careers board (public API). token = the name in jobs.ashbyhq.com/{token}.</summary>
    private static SourceProvider Ashby(string token, string company, string[] regions) => new(
        Id: $"ashby-{token}",
        Name: $"{company} (careers)",
        Type: FetchType.Api,
        BuildUrl: _ => $"https://api.ashbyhq.com/posting-api/job-board/{token}",
        KeywordExamples: ["engineer", "frontend", "data"],
        Regions: regions,
        Api: new ApiSourceConfig
        {
            ItemsPath = "jobs",
            Company = company,
            FieldMap =
            {
                ["id"] = "id",
                ["title"] = "title",
                ["url"] = "jobUrl",
                ["location"] = "location",
                ["description"] = "descriptionPlain",
                ["posted"] = "publishedAt",
                ["employment"] = "employmentType"
            }
        });

    // ------------------------------------------------------------------ lookup / keyword helpers (unchanged)

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