using System.Globalization;
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
        var keyword = ctx.Source.Keyword ?? "";

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

        var node = Walk(doc.RootElement, cfg.ItemsPath);
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

            if (!Uri.TryCreate(baseUri, rawUrl, out var absolute)) continue;
            var url = absolute.ToString();

            var externalId = Blank(Read(item, cfg.FieldMap, "id")) ?? url;

            DateTime? posted = DateTime.TryParse(
                Read(item, cfg.FieldMap, "posted"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt
                : null;

            // Only a plain number counts; free text like "$31k - $52k" stays unparsed (null).
            int? salary = decimal.TryParse(
                Read(item, cfg.FieldMap, "salary"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                ? (int)Math.Round(amount)
                : null;

            result.Add(new FetchedJob(
                externalId,
                title,
                Blank(Read(item, cfg.FieldMap, "company")),
                Blank(Read(item, cfg.FieldMap, "location")),
                url,
                TextUtil.CleanHtml(Read(item, cfg.FieldMap, "description")),
                posted,
                salary,
                Blank(Read(item, cfg.FieldMap, "employment"))));
        }

        return result;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Follows a dot-separated path ("data.jobs", "items.0.id"). Null when any step is missing.</summary>
    private static JsonElement? Walk(JsonElement el, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return el;

        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(part, out var next))
                el = next;
            else if (el.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i >= 0 && i < el.GetArrayLength())
                el = el[i];
            else
                return null;
        }

        return el;
    }

    private static string? Read(JsonElement item, Dictionary<string, string> map, string field)
    {
        if (!map.TryGetValue(field, out var path)) return null;

        var el = Walk(item, path);
        return el?.ValueKind switch
        {
            JsonValueKind.String => el.Value.GetString(),
            JsonValueKind.Number => el.Value.GetRawText(),
            _ => null
        };
    }
}
