using System.Text;

namespace WorkGreg.Filtering;

/// <summary>
/// The one whole-word matcher, shared by the profile keyword and every filter list (Include, Exclude, Companies,
/// Locations, remote markers). Text and terms are normalized the same way, so they can't disagree:
/// lower-cased, punctuation ignored, so "full stack" matches "Full-Stack" and "Fullstack", and ".NET" doesn't match "network".
/// A trailing '+' or '#' stays part of a word ("C#", "C++"); a leading one is dropped ("#remote" is "remote").
///
/// A term ending in '*' is a word prefix: "remote*" matches "remote", "remotely", "remote-first".
/// </summary>
public static class WordMatcher
{
    public const char PrefixMarker = '*';

    /// <summary>
    /// Joins the parts and normalizes them to single-spaced lower-case words, padded with one space on both sides.
    /// Normalize once, then call <see cref="Contains"/> for as many terms as needed.
    /// </summary>
    public static string Normalize(params string?[] parts)
    {
        var text = string.Join(' ', parts.Where(p => !string.IsNullOrEmpty(p)));
        var sb = new StringBuilder(text.Length + 2);
        sb.Append(' ');

        for (var i = 0; i < text.Length; i++)
        {
            var ch = char.ToLowerInvariant(text[i]);
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (IsTrailingSign(text, i, sb)) sb.Append(ch);
            else if (sb[sb.Length - 1] != ' ') sb.Append(' ');
        }

        if (sb[sb.Length - 1] != ' ') sb.Append(' ');
        return sb.ToString();
    }

    /// <summary>False for a term with no letters or digits at all (blank, or only punctuation).</summary>
    public static bool HasWords(string? term) => !string.IsNullOrWhiteSpace(term) && Parse(term).Core.Length > 0;

    /// <summary>True if <paramref name="term"/> occurs as whole words in text produced by <see cref="Normalize"/>.</summary>
    public static bool Contains(string normalizedText, string term)
    {
        var (core, prefix) = Parse(term);
        if (core.Length == 0) return false;

        if (ContainsCore(normalizedText, core, prefix)) return true;

        // "full stack" -> "fullstack"
        return core.Contains(' ') && ContainsCore(normalizedText, core.Replace(" ", ""), prefix);
    }

    /// <summary>Normalizes <paramref name="text"/> and checks one term. Prefer <see cref="Contains"/> when testing several terms.</summary>
    public static bool Matches(string? text, string term) => Contains(Normalize(text), term);

    public static bool ContainsAny(string normalizedText, IEnumerable<string> terms) =>
        terms.Any(t => Contains(normalizedText, t));

    /// <summary>The terms that occur in the text, in their original order and spelling.</summary>
    public static List<string> FindAll(string normalizedText, IEnumerable<string> terms) =>
        terms.Where(t => Contains(normalizedText, t)).ToList();

    private static bool ContainsCore(string text, string core, bool prefix) =>
        text.Contains(prefix ? " " + core : " " + core + " ", StringComparison.Ordinal);

    private static (string Core, bool Prefix) Parse(string term)
    {
        var trimmed = term.Trim();
        var prefix = trimmed.EndsWith(PrefixMarker);
        return (Normalize(prefix ? trimmed[..^1] : trimmed).Trim(), prefix);
    }

    // '#' / '+' count as part of a word only when they close it: "c#", "c++", "5+", not "#hiring" or "a+b".
    private static bool IsTrailingSign(string text, int i, StringBuilder sb)
    {
        var ch = text[i];
        if (ch is not ('#' or '+')) return false;

        var prev = sb[sb.Length - 1];
        if (!(char.IsLetterOrDigit(prev) || prev == ch)) return false;

        return i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1]);
    }
}