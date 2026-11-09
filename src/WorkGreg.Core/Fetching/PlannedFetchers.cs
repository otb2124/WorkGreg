using WorkGreg.Domain;

namespace WorkGreg.Fetching;

// Placeholders so the registry and the bot already know these strategies exist.
// Replace the body of FetchAsync and flip IsAvailable when you build them.
// Site-specific settings (selectors etc.) come from ctx.Provider.Html in SourceCatalog.

/// <summary>Plain HttpClient + AngleSharp / embedded JSON. Selectors live in SourceProvider.Html.</summary>
public sealed class HtmlFetcher : IJobFetcher
{
    public FetchType Type => FetchType.Html;
    public bool IsAvailable => false;

    public Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct) =>
        throw new NotSupportedException("Html fetcher is not implemented yet.");
}

/// <summary>Playwright for client-rendered or bot-protected sites.</summary>
public sealed class BrowserFetcher : IJobFetcher
{
    public FetchType Type => FetchType.Browser;
    public bool IsAvailable => false;

    public Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct) =>
        throw new NotSupportedException("Browser fetcher is not implemented yet.");
}
