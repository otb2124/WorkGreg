namespace WorkGreg.Domain;

/// <summary>Config for FetchType.Html and FetchType.Browser. Lives on SourceProvider.Html in the catalog.</summary>
public sealed class HtmlSourceConfig
{
    public string ItemSelector { get; set; } = "";
    public string TitleSelector { get; set; } = "";
    public string UrlSelector { get; set; } = "";
    public string? CompanySelector { get; set; }
    public string? LocationSelector { get; set; }
    public string? DescriptionSelector { get; set; }
    public string? PostedSelector { get; set; }
    /// <summary>DateTime.TryParseExact format, e.g. "yyyy-MM-dd" or "dd.MM.yyyy".</summary>
    public string? PostedFormat { get; set; }
    /// <summary>Used to resolve relative links. Defaults to Source.Url.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>Extra CSS selectors for filtering items client-side, optional.</summary>
    public string? WaitForSelector { get; set; }
}

/// <summary>Config for FetchType.Api. Lives on SourceProvider.Api in the catalog.</summary>
public sealed class ApiSourceConfig
{
    public string Method { get; set; } = "GET";
    /// <summary>Optional request body. "{keyword}" is replaced with the JSON-escaped search keyword.</summary>
    public string? Body { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Dot-separated path to the array of items in the JSON response, e.g. "data.jobs".</summary>
    public string ItemsPath { get; set; } = "";
    /// <summary>
    /// Maps our normalized field names to dot-separated paths in each item:
    /// title, url (both required), id, company, location, description, posted, salary (numeric only), employment.
    /// </summary>
    public Dictionary<string, string> FieldMap { get; set; } = new();
}