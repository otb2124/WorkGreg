using Microsoft.Extensions.Options;

namespace WorkGreg.Client;

/// <summary>Bound from <c>Core</c>: where the job watcher's REST API lives and the key it expects.</summary>
public sealed class CoreOptions
{
    /// <summary>For example http://localhost:5080 (the core's Urls setting).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Sent as the X-Api-Key header. Must equal the core's Api:ApiKey.</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class CoreOptionsValidator : IValidateOptions<CoreOptions>
{
    public ValidateOptionsResult Validate(string? name, CoreOptions options)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("Core:BaseUrl must be an absolute http(s) URL, e.g. http://localhost:5080.");

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            errors.Add("Core:ApiKey is not set. Use secrets.json or the Core__ApiKey env var (the same value as the core's Api:ApiKey).");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
