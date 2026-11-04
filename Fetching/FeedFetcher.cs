using System.ServiceModel.Syndication;
using System.Xml;
using WorkGreg.Domain;

namespace WorkGreg.Fetching;

/// <summary>Reads RSS 2.0 / Atom feeds (e.g. DOU vacancy feeds, saved-search feeds).</summary>
public sealed class FeedFetcher(IHttpClientFactory httpFactory, HostThrottle throttle) : IJobFetcher
{
    public FetchType Type => FetchType.Feed;
    public bool IsAvailable => true;

    public async Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("fetch");
        var bytes = await throttle.RunAsync(ctx.Url, () => http.GetByteArrayAsync(ctx.Url, ct), ct);

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
