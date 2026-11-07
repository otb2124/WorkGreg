using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkGreg.Domain;

/// <summary>
/// Fingerprint used to skip the same vacancy appearing on several boards (or in several profiles of one user).
/// </summary>
public static partial class JobHasher
{
    /// <param name="company">Null/blank for sources that don't expose a company (RSS feeds).</param>
    /// <param name="title">Vacancy title.</param>
    /// <param name="scopeKey">
    /// Used only when there is no company: two different employers can both post "Junior Developer", so without a
    /// company the hash is limited to one source (provider id) instead of matching titles across the whole world.
    /// </param>
    public static string Compute(string? company, string title, string scopeKey)
    {
        // With a company the formula is exactly the old one, so hashes already stored in the database keep matching.
        var basis = string.IsNullOrWhiteSpace(company)
            ? $"nocompany{scopeKey}{title}"
            : $"{company}{title}";

        var normalized = NonAlnum().Replace(basis.ToLowerInvariant(), "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]")] private static partial Regex NonAlnum();
}