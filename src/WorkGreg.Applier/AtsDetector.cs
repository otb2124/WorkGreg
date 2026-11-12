using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>Tells which applicant-tracking system a vacancy uses: by the page's host first, then by the provider id prefix.</summary>
public static class AtsDetector
{
    private static readonly Dictionary<string, AtsKind> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["boards.greenhouse.io"] = AtsKind.Greenhouse,
        ["job-boards.greenhouse.io"] = AtsKind.Greenhouse,
        ["jobs.lever.co"] = AtsKind.Lever,
        ["jobs.ashbyhq.com"] = AtsKind.Ashby
    };

    private static readonly (string Prefix, AtsKind Kind)[] ProviderPrefixes =
    [
        ("gh-", AtsKind.Greenhouse),
        ("lever-", AtsKind.Lever),
        ("ashby-", AtsKind.Ashby)
    ];

    /// <returns><see cref="AtsKind.Unknown"/> when neither the URL nor the provider id is recognised.</returns>
    public static AtsKind Detect(string? url, string? providerId = null)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && Hosts.TryGetValue(uri.Host, out var byHost))
            return byHost;

        if (!string.IsNullOrWhiteSpace(providerId))
            foreach (var (prefix, kind) in ProviderPrefixes)
                if (providerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return kind;

        return AtsKind.Unknown;
    }
}
