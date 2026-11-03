using System.Net;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using WorkGreg.Domain;

namespace WorkGreg.Fetching;

/// <summary>Reads RSS 2.0 / Atom feeds (e.g. DOU vacancy feeds, saved-search feeds).</summary>
public sealed partial class FeedFetcher(IHttpClientFactory httpFactory) : IJobFetcher
{
    public FetchType Type => FetchType.Feed;
    public bool IsAvailable => true;

    public async Task<IReadOnlyList<FetchedJob>> FetchAsync(Source source, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("fetch");
        await using var stream = await http.GetStreamAsync(source.Url, ct);

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
            var description = Clean(rawDescription);

            DateTime? posted = item.PublishDate != DateTimeOffset.MinValue
                ? item.PublishDate.UtcDateTime
                : item.LastUpdatedTime != DateTimeOffset.MinValue ? item.LastUpdatedTime.UtcDateTime : null;

            // Feeds rarely expose company/location as separate fields; they stay inside title/description.
            result.Add(new FetchedJob(externalId, title, null, null, link, description, posted));
        }

        return result;
    }

    private static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > 2000 ? text[..2000] : text;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
}
