using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WorkGreg.Domain;

namespace WorkGreg.Fetching;

/// <summary>
/// Generic JSON API fetcher. Everything site-specific (items path, field names, headers, body)
/// comes from SourceProvider.Api in the catalog.
/// </summary>
public sealed class ApiFetcher(IHttpClientFactory httpFactory, HostThrottle throttle) : IJobFetcher
{
    public FetchType Type => FetchType.Api;
    public bool IsAvailable => true;

    public async Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Provider?.Api
                  ?? throw new InvalidOperationException("This source has no ApiSourceConfig in SourceCatalog.");

        if (!cfg.FieldMap.ContainsKey("title") || !cfg.FieldMap.ContainsKey("url"))
            throw new InvalidOperationException("ApiSourceConfig.FieldMap must map at least 'title' and 'url'.");

        var http = httpFactory.CreateClient("fetch");
        var keyword = ctx.Keyword;

        var json = await throttle.RunAsync(ctx.Url, async () =>
        {
            using var req = new HttpRequestMessage(new HttpMethod(cfg.Method), ctx.Url);

            foreach (var (name, value) in cfg.Headers)
                req.Headers.TryAddWithoutValidation(name, value);

            if (cfg.Body is not null)
            {
                var escaped = JsonSerializer.Serialize(keyword)[1..^1]; // JSON-escaped, without the quotes
                req.Content = new StringContent(cfg.Body.Replace("{keyword}", escaped), Encoding.UTF8, "application/json");
            }

            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }, ct);

        using var doc = JsonDocument.Parse(json);

        var node = JsonPath.Walk(doc.RootElement, cfg.ItemsPath);
        if (node is null && cfg.MissingItemsIsEmpty)
            return [];
        if (node is not { ValueKind: JsonValueKind.Array } items)
            throw new InvalidOperationException(
                $"'{cfg.ItemsPath}' is not an array in the response; the API format probably changed.");

        var baseUri = new Uri(ctx.Url);
        var result = new List<FetchedJob>();

        foreach (var item in items.EnumerateArray())
        {
            var title = Blank(Read(item, cfg.FieldMap, "title"));
            var rawUrl = Blank(Read(item, cfg.FieldMap, "url"));
            if (title is null || rawUrl is null) continue;

            if (cfg.UrlTemplate is not null)
                rawUrl = cfg.UrlTemplate.Replace("{url}", rawUrl);

            if (!Uri.TryCreate(baseUri, rawUrl, out var absolute)) continue;
            var url = absolute.ToString();

            var externalId = Blank(Read(item, cfg.FieldMap, "id")) ?? url;

            // Some APIs (Greenhouse) return HTML as escaped text ("&lt;p&gt;"). Decode first so CleanHtml can strip the tags.
            var rawDescription = Read(item, cfg.FieldMap, "description");
            if (rawDescription is not null && rawDescription.Contains("&lt;", StringComparison.Ordinal))
                rawDescription = WebUtility.HtmlDecode(rawDescription);

            result.Add(new FetchedJob(
                externalId,
                title,
                Blank(Read(item, cfg.FieldMap, "company")) ?? cfg.Company,
                Blank(Read(item, cfg.FieldMap, "location")),
                url,
                TextUtil.CleanHtml(rawDescription),
                DateParsing.Parse(Read(item, cfg.FieldMap, "posted")),
                // Only a plain number counts; free text like "$31k - $52k" stays unparsed (null).
                decimal.TryParse(Read(item, cfg.FieldMap, "salary"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                    ? (int)Math.Round(amount)
                    : null,
                Blank(Read(item, cfg.FieldMap, "employment"))));
        }

        return result;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The item's value for one of our normalized field names, via the provider's FieldMap. Null when unmapped or missing.</summary>
    private static string? Read(JsonElement item, Dictionary<string, string> map, string field) =>
        map.TryGetValue(field, out var path) ? JsonPath.ReadString(item, path) : null;
}