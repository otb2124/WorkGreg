namespace WorkGreg.Domain;

/// <summary>
/// A website we know how to search. Loaded from providers.json (see <see cref="SourceCatalogLoader"/>):
/// users pick a source from the catalog and give a keyword, they never paste raw URLs.
/// </summary>
/// <param name="Id">Stable id stored in Source.Provider and typed in commands, e.g. "dou".</param>
/// <param name="Name">Display name, e.g. "DOU.ua".</param>
/// <param name="Type">How it is fetched; the matching IJobFetcher must exist and be available.</param>
/// <param name="UrlTemplate">The request URL. "{keyword}" is replaced with the URL-escaped keyword, see <see cref="BuildUrl"/>.</param>
/// <param name="KeywordExamples">Shown as hints when asking for a keyword.</param>
/// <param name="Html">Selectors for FetchType.Html / Browser providers.</param>
/// <param name="Api">Endpoint mapping for FetchType.Api providers.</param>
/// <param name="Regions">Region ids (see <see cref="SourceCatalog.Regions"/>) this source is listed under. Null = "global".</param>
public sealed record SourceProvider(
    string Id,
    string Name,
    FetchType Type,
    string UrlTemplate,
    IReadOnlyList<string> KeywordExamples,
    HtmlSourceConfig? Html = null,
    ApiSourceConfig? Api = null,
    IReadOnlyList<string>? Regions = null)
{
    /// <summary>Fills the URL template with a (normalized) keyword. Templates without "{keyword}" return the same URL for every keyword.</summary>
    public string BuildUrl(string keyword) =>
        UrlTemplate.Replace(SourceCatalog.KeywordPlaceholder, Uri.EscapeDataString(keyword), StringComparison.Ordinal);
}

public sealed record SourceRegion(string Id, string Label);

/// <summary>An immutable, already validated catalog: regions + providers with an id index.</summary>
public sealed class CatalogData
{
    private readonly Dictionary<string, SourceProvider> _byId = new(StringComparer.OrdinalIgnoreCase);

    public CatalogData(IReadOnlyList<SourceRegion> regions, IReadOnlyList<SourceProvider> providers)
    {
        Regions = regions;
        Providers = providers;
        foreach (var p in providers) _byId.TryAdd(p.Id, p);   // duplicates are reported by CatalogValidator
    }

    /// <summary>Menu order. A region with no available sources is hidden by the bot.</summary>
    public IReadOnlyList<SourceRegion> Regions { get; }

    public IReadOnlyList<SourceProvider> Providers { get; }

    public SourceProvider? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var provider) ? provider : null;
}

/// <summary>
/// Static facade over the catalog so call sites stay short (SourceCatalog.Find, SourceCatalog.All ...).
/// The data lives in providers.json. <see cref="Load"/> reads and validates it at startup (the app refuses to start
/// on a bad file); if nobody loaded it yet, the first access loads the default file next to the executable.
/// </summary>
public static class SourceCatalog
{
    public const int MaxKeywordLength = 80;

    /// <summary>The only placeholder a provider URL may contain.</summary>
    public const string KeywordPlaceholder = "{keyword}";

    public const string DefaultFileName = "providers.json";

    private static readonly object Gate = new();
    private static CatalogData? _current;

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, DefaultFileName);

    public static IReadOnlyList<SourceRegion> Regions => Current.Regions;

    /// <summary>To support a new website: add one entry to providers.json (and a fetcher, if its FetchType is new).</summary>
    public static IReadOnlyList<SourceProvider> All => Current.Providers;

    public static IReadOnlyList<string> RegionsOf(SourceProvider p) => p.Regions ?? new[] { "global" };

    /// <summary>Reads, validates and activates the catalog file. Throws <see cref="CatalogException"/> listing every problem.</summary>
    public static CatalogData Load(string path)
    {
        var data = SourceCatalogLoader.LoadFile(path);
        Install(data);
        return data;
    }

    /// <summary>Activates an already built catalog (tests can use this with SourceCatalogLoader.Parse).</summary>
    public static void Install(CatalogData data)
    {
        lock (Gate) _current = data;
    }

    private static CatalogData Current
    {
        get
        {
            var data = Volatile.Read(ref _current);
            if (data is not null) return data;

            lock (Gate)
                return _current ??= SourceCatalogLoader.LoadFile(DefaultPath);
        }
    }

    // ------------------------------------------------------------------ lookup / keyword helpers

    public static SourceProvider? Find(string? id) => Current.Find(id);

    /// <summary>Trims and collapses whitespace. Null when empty or longer than <see cref="MaxKeywordLength"/>.</summary>
    public static string? NormalizeKeyword(string? raw)
    {
        var keyword = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return keyword.Length is > 0 and <= MaxKeywordLength ? keyword : null;
    }

    public static string DisplayName(SourceProvider provider, string keyword) => $"{provider.Name} · {keyword}";
}