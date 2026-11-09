using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkGreg.Domain;

/// <summary>
/// The startup check for providers.json. Returns every problem as a plain-text line (empty = the catalog is fine).
/// Pure, so tests can call it too. Rules:
/// unique ids (case-insensitive), sane id/name/url/examples, known regions, and per FetchType the config it needs:
/// Api needs a FieldMap with at least "title" and "url"; Html/Browser need item, title and url selectors.
/// </summary>
public static partial class CatalogValidator
{
    private const int MaxIdLength = 32;

    /// <summary>The normalized field names an ApiSourceConfig.FieldMap may use (see ApiFetcher).</summary>
    private static readonly string[] FieldNames =
        ["id", "title", "url", "company", "location", "description", "posted", "salary", "employment"];

    public static IReadOnlyList<string> Validate(CatalogData data)
    {
        var errors = new List<string>();

        var regionIds = ValidateRegions(data.Regions, errors);

        if (data.Providers.Count == 0)
            errors.Add("The catalog has no providers.");

        foreach (var duplicate in data.Providers
                     .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Key.Length > 0 && g.Count() > 1))
            errors.Add($"Provider id '{duplicate.Key}' is used {duplicate.Count()} times; ids must be unique (case-insensitive).");

        var index = 0;
        foreach (var p in data.Providers)
        {
            index++;
            ValidateProvider(p, index, regionIds, errors);
        }

        return errors;
    }

    /// <summary>Every provider's FetchType must have a registered fetcher, otherwise polling it would throw later.</summary>
    public static void RequireFetchers(IEnumerable<SourceProvider> providers, IEnumerable<FetchType> registered)
    {
        var have = registered.ToHashSet();
        var errors = providers
            .Where(p => !have.Contains(p.Type))
            .Select(p => $"provider '{p.Id}' has type {p.Type} but no IJobFetcher is registered for it.")
            .ToList();

        if (errors.Count > 0)
            throw new CatalogException(errors, SourceCatalog.DefaultFileName);
    }

    // ------------------------------------------------------------------ regions

    private static HashSet<string> ValidateRegions(IReadOnlyList<SourceRegion> regions, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in regions)
        {
            if (string.IsNullOrWhiteSpace(r.Id))
                errors.Add("A region has no id.");
            else if (!IdFormat().IsMatch(r.Id))
                errors.Add($"Region id '{r.Id}' must be lowercase letters, digits and '-' only.");
            else if (!ids.Add(r.Id))
                errors.Add($"Region id '{r.Id}' is used more than once.");

            if (string.IsNullOrWhiteSpace(r.Label))
                errors.Add($"Region '{r.Id}' has no label.");
        }

        // RegionsOf() falls back to "global" for providers without regions.
        if (!ids.Contains("global"))
            errors.Add("The regions must include 'global': providers without a 'regions' list are listed there.");

        return ids;
    }

    // ------------------------------------------------------------------ providers

    private static void ValidateProvider(SourceProvider p, int index, HashSet<string> regionIds, List<string> errors)
    {
        var who = p.Id.Length > 0 ? $"provider '{p.Id}'" : $"provider #{index}";

        if (p.Id.Length == 0)
            errors.Add($"{who}: 'id' is missing.");
        else if (!IdFormat().IsMatch(p.Id) || p.Id.Length > MaxIdLength)
            errors.Add($"{who}: the id must be lowercase letters, digits and '-' only, at most {MaxIdLength} characters.");

        if (p.Name.Length == 0)
            errors.Add($"{who}: 'name' is missing.");

        if (p.KeywordExamples.Count == 0 || p.KeywordExamples.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{who}: 'keywordExamples' needs at least one non-empty example.");

        if (p.Regions is not null)
        {
            if (p.Regions.Count == 0)
                errors.Add($"{who}: 'regions' is empty; leave it out to list the provider under 'global'.");

            foreach (var r in p.Regions.Where(r => !regionIds.Contains(r)))
                errors.Add($"{who}: unknown region '{r}'.");
        }

        ValidateUrl(p, who, errors);

        switch (p.Type)
        {
            case FetchType.Feed:
                if (p.Api is not null) errors.Add($"{who}: type Feed must not have an 'api' section.");
                if (p.Html is not null) errors.Add($"{who}: type Feed must not have an 'html' section.");
                break;

            case FetchType.Api:
                if (p.Html is not null) errors.Add($"{who}: type Api must not have an 'html' section.");
                if (p.Api is null)
                    errors.Add($"{who}: type Api needs an 'api' section (with at least a fieldMap for 'title' and 'url').");
                else
                    ValidateApi(p.Api, who, errors);
                break;

            case FetchType.Html:
            case FetchType.Browser:
                if (p.Api is not null) errors.Add($"{who}: type {p.Type} must not have an 'api' section.");
                if (p.Html is null)
                    errors.Add($"{who}: type {p.Type} needs an 'html' section.");
                else
                    ValidateHtml(p.Html, who, errors);
                break;

            default:
                errors.Add($"{who}: unsupported type {p.Type}.");
                break;
        }
    }

    private static void ValidateUrl(SourceProvider p, string who, List<string> errors)
    {
        var template = p.UrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add($"{who}: 'url' is missing.");
            return;
        }

        foreach (Match placeholder in PlaceholderPattern().Matches(template))
        {
            if (placeholder.Value != SourceCatalog.KeywordPlaceholder)
                errors.Add($"{who}: url contains the unknown placeholder {placeholder.Value}; only {SourceCatalog.KeywordPlaceholder} is supported.");
        }

        if (!HostPattern().IsMatch(template))
        {
            errors.Add($"{who}: url must start with http:// or https:// followed by a fixed host (no placeholder in the host).");
            return;
        }

        if (!Uri.TryCreate(p.BuildUrl("full stack .net"), UriKind.Absolute, out _))
            errors.Add($"{who}: url is not a valid absolute URL once the keyword is filled in.");
    }

    private static void ValidateApi(ApiSourceConfig api, string who, List<string> errors)
    {
        if (api.Method is not ("GET" or "POST"))
            errors.Add($"{who}: api.method must be GET or POST, not '{api.Method}'.");

        // The rule from the task: every FieldMap has title and url.
        foreach (var required in new[] { "title", "url" })
        {
            if (!api.FieldMap.TryGetValue(required, out var path) || string.IsNullOrWhiteSpace(path))
                errors.Add($"{who}: api.fieldMap must map '{required}'.");
        }

        foreach (var (field, path) in api.FieldMap)
        {
            if (!FieldNames.Contains(field))
                errors.Add($"{who}: api.fieldMap has the unknown field '{field}' (allowed: {string.Join(", ", FieldNames)}).");
            else if (string.IsNullOrWhiteSpace(path))
                errors.Add($"{who}: api.fieldMap['{field}'] is empty.");
        }

        if (api.Headers.Any(h => string.IsNullOrWhiteSpace(h.Key)))
            errors.Add($"{who}: api.headers has an empty header name.");

        if (api.UrlTemplate is not null && !api.UrlTemplate.Contains("{url}", StringComparison.Ordinal))
            errors.Add($"{who}: api.urlTemplate must contain {{url}} (it is replaced with the mapped 'url' value).");

        if (api.Body is not null)
        {
            // ApiFetcher replaces {keyword} with the JSON-escaped keyword and sends the result as application/json.
            try
            {
                using var doc = JsonDocument.Parse(api.Body.Replace(SourceCatalog.KeywordPlaceholder, "x", StringComparison.Ordinal));
            }
            catch (JsonException)
            {
                errors.Add($"{who}: api.body is not valid JSON (after {SourceCatalog.KeywordPlaceholder} is replaced).");
            }
        }
    }

    private static void ValidateHtml(HtmlSourceConfig html, string who, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(html.ItemSelector)) errors.Add($"{who}: html.itemSelector is missing.");
        if (string.IsNullOrWhiteSpace(html.TitleSelector)) errors.Add($"{who}: html.titleSelector is missing.");
        if (string.IsNullOrWhiteSpace(html.UrlSelector)) errors.Add($"{who}: html.urlSelector is missing.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdFormat();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^https?://[^/?#{}\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();
}