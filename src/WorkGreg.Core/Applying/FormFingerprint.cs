using System.Security.Cryptography;
using System.Text;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>
/// A short identity of a form's shape. Phase 4 compares it before submitting: a different fingerprint means the form
/// changed since the user looked at the preview.
/// </summary>
public static class FormFingerprint
{
    /// <summary>SHA-256 (lowercase hex) of the normalized, non-empty labels, sorted ordinally and joined by newlines.</summary>
    public static string Compute(IEnumerable<string?> labels)
    {
        var normalized = labels
            .Select(FieldMatcher.Normalize)
            .Where(l => l.Length > 0)
            .OrderBy(l => l, StringComparer.Ordinal);

        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', normalized));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
