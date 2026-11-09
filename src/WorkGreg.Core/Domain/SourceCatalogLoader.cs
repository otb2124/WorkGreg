using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkGreg.Domain;

/// <summary>Thrown when providers.json is missing or wrong. The message lists every problem found, not just the first.</summary>
public sealed class CatalogException(IReadOnlyList<string> errors, string source)
    : Exception(Format(errors, source))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    private static string Format(IReadOnlyList<string> errors, string source) =>
        $"The provider catalog '{source}' is invalid:{Environment.NewLine}" +
        string.Join(Environment.NewLine, errors.Select(e => "  - " + e));
}

/// <summary>
/// Reads providers.json and turns it into <see cref="CatalogData"/>:
/// parse -> resolve "api.extends" templates -> <see cref="CatalogValidator"/>. Any problem throws <see cref="CatalogException"/>.
/// </summary>
public static class SourceCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // providers.json may contain // comments
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,   // a typo like "feildMap" is an error, not silently ignored
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static CatalogData LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new CatalogException([$"File not found: {path}"], path);

        return Parse(File.ReadAllText(path), path);
    }

    /// <param name="sourceName">Only used in error messages.</param>
    public static CatalogData Parse(string json, string sourceName = SourceCatalog.DefaultFileName)
    {
        CatalogFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize<CatalogFileJson>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogException([$"Not valid catalog JSON: {ex.Message}"], sourceName);
        }

        if (file is null)
            throw new CatalogException(["The file is empty."], sourceName);

        var errors = new List<string>();
        var data = Build(file, errors);

        // Only validate what was built successfully; structural errors (unknown template, missing type) are already listed.
        errors.AddRange(CatalogValidator.Validate(data));

        if (errors.Count > 0)
            throw new CatalogException(errors, sourceName);

        return data;
    }

    // ------------------------------------------------------------------ JSON -> domain

    private static CatalogData Build(CatalogFileJson file, List<string> errors)
    {
        var regions = (file.Regions ?? [])
            .Select(r => new SourceRegion(r.Id?.Trim() ?? "", r.Label?.Trim() ?? ""))
            .ToList();

        var templates = new Dictionary<string, ApiJson>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, template) in file.ApiTemplates ?? new())
        {
            if (template.Extends is not null)
                errors.Add($"apiTemplates.{name}: a template can't extend another template.");
            if (!templates.TryAdd(name, template))
                errors.Add($"apiTemplates: '{name}' is defined twice (names are case-insensitive).");
        }

        var providers = new List<SourceProvider>();
        var index = 0;
        foreach (var p in file.Providers ?? [])
        {
            index++;
            var owner = string.IsNullOrWhiteSpace(p.Id) ? $"provider #{index}" : $"provider '{p.Id}'";

            if (p.Type is null)
            {
                errors.Add($"{owner}: 'type' is missing (Feed, Api, Html or Browser).");
                continue;
            }

            providers.Add(new SourceProvider(
                Id: p.Id?.Trim() ?? "",
                Name: p.Name?.Trim() ?? "",
                Type: p.Type.Value,
                UrlTemplate: p.Url?.Trim() ?? "",
                KeywordExamples: (p.KeywordExamples ?? []).Select(k => k.Trim()).ToList(),
                Html: p.Html,
                Api: ResolveApi(owner, p.Api, templates, errors),
                Regions: p.Regions?.Select(r => r.Trim()).ToList()));
        }

        return new CatalogData(regions, providers);
    }

    /// <summary>Starts from the named template (if any), then applies the provider's own fields on top. Every provider gets its own copy.</summary>
    private static ApiSourceConfig? ResolveApi(
        string owner, ApiJson? api, Dictionary<string, ApiJson> templates, List<string> errors)
    {
        if (api is null) return null;

        var config = new ApiSourceConfig();

        if (api.Extends is not null)
        {
            if (templates.TryGetValue(api.Extends, out var baseJson))
                Apply(config, baseJson);
            else
                errors.Add($"{owner}: api.extends '{api.Extends}' is not defined in apiTemplates.");
        }

        Apply(config, api);
        return config;
    }

    private static void Apply(ApiSourceConfig target, ApiJson source)
    {
        if (source.Method is not null) target.Method = source.Method.Trim().ToUpperInvariant();
        if (source.Body is not null) target.Body = source.Body;
        if (source.ItemsPath is not null) target.ItemsPath = source.ItemsPath;   // "" is meaningful: the root is the array
        if (source.Company is not null) target.Company = source.Company;
        if (source.UrlTemplate is not null) target.UrlTemplate = source.UrlTemplate;
        if (source.MissingItemsIsEmpty is { } missing) target.MissingItemsIsEmpty = missing;

        if (source.Headers is not null)
            foreach (var (name, value) in source.Headers) target.Headers[name] = value;

        if (source.FieldMap is not null)
            foreach (var (field, path) in source.FieldMap) target.FieldMap[field] = path;
    }
}

// ---------------------------------------------------------------------- file shape (internal, only for deserializing)

internal sealed class CatalogFileJson
{
    public List<RegionJson>? Regions { get; set; }
    public Dictionary<string, ApiJson>? ApiTemplates { get; set; }
    public List<ProviderJson>? Providers { get; set; }
}

internal sealed class RegionJson
{
    public string? Id { get; set; }
    public string? Label { get; set; }
}

internal sealed class ProviderJson
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public FetchType? Type { get; set; }
    public string? Url { get; set; }
    public List<string>? KeywordExamples { get; set; }
    public List<string>? Regions { get; set; }
    public ApiJson? Api { get; set; }
    public HtmlSourceConfig? Html { get; set; }

    /// <summary>Free text for humans (why a source is odd, whether it was test-fetched). Ignored by the app.</summary>
    public string? Note { get; set; }
}

internal sealed class ApiJson
{
    /// <summary>Name of an entry in apiTemplates to start from.</summary>
    public string? Extends { get; set; }
    public string? Method { get; set; }
    public string? Body { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? ItemsPath { get; set; }
    public Dictionary<string, string>? FieldMap { get; set; }
    public string? Company { get; set; }
    public string? UrlTemplate { get; set; }
    public bool? MissingItemsIsEmpty { get; set; }
}