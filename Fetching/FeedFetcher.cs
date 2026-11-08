using System.Globalization;
using System.Net;
using System.ServiceModel.Syndication;
using System.Xml;
using WorkGreg.Domain;

namespace WorkGreg.Fetching;

/// <summary>
/// Reads RSS 2.0 / Atom feeds (e.g. DOU vacancy feeds, saved-search feeds).
/// Sends If-None-Match / If-Modified-Since when it has seen the feed before (see <see cref="FeedCache"/>).
/// Retries of transient errors (503, timeouts...) happen inside the "fetch" HttpClient, see AddFetching.
/// </summary>
public sealed class FeedFetcher(IHttpClientFactory httpFactory, HostThrottle throttle, FeedCache cache) : IJobFetcher
{
    public FetchType Type => FetchType.Feed;
    public bool IsAvailable => true;

    /// <summary>Body is null when the server answered 304 Not Modified.</summary>
    private sealed record Response(byte[]? Body, string? ETag, string? LastModified);

    public async Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("fetch");
        var cached = cache.Get(ctx.Url);

        var response = await throttle.RunAsync(ctx.Url, async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ctx.Url);

            if (cached?.ETag is { } etag)
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (cached?.LastModified is { } lastModified)
                req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

            using var resp = await http.SendAsync(req, ct);

            // We only send validators when we hold the previous result, so a 304 always has something to return.
            if (resp.StatusCode == HttpStatusCode.NotModified && cached is not null)
                return new Response(null, cached.ETag, cached.LastModified);

            resp.EnsureSuccessStatusCode();

            return new Response(
                await resp.Content.ReadAsByteArrayAsync(ct),
                resp.Headers.ETag?.ToString(),
                resp.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture));
        }, ct);

        if (response.Body is null)
            return cached?.Items ?? [];

        var items = Parse(response.Body);

        // Remember the result only when the server gave us something to validate with; otherwise forget any old entry.
        if (response.ETag is not null || response.LastModified is not null)
            cache.Set(ctx.Url, new FeedCache.Entry(response.ETag, response.LastModified, items));
        else
            cache.Remove(ctx.Url);

        return items;
    }

    private static List<FetchedJob> Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        });

        var feed = SyndicationFeed.Load(reader);
        var result = new List<FetchedJob>();

        foreach (var item in feed.Items)
        {
            var link = item.Links.FirstOrDefault(l => l.RelationshipType is null or "alternate")?.Uri.ToString()
                       ?? item.Links.FirstOrDefault()?.Uri.ToString();

            var externalId = !string.IsNullOrWhiteSpace(item.Id) ? item.Id : link;
            var title = item.Title?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(title) || link is null)
                continue;

            var rawDescription = item.Summary?.Text ?? (item.Content as TextSyndicationContent)?.Text;
            var description = TextUtil.CleanHtml(rawDescription);

            DateTime? posted = item.PublishDate != DateTimeOffset.MinValue
                ? item.PublishDate.UtcDateTime
                : item.LastUpdatedTime != DateTimeOffset.MinValue ? item.LastUpdatedTime.UtcDateTime : null;

            // Feeds rarely expose company/location as separate fields; they stay inside title/description.
            result.Add(new FetchedJob(externalId, title, null, null, link, description, posted));
        }

        return result;
    }
}