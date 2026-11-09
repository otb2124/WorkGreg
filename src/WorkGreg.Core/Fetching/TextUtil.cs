using System.Net;
using System.Text.RegularExpressions;

namespace WorkGreg.Fetching;

public static partial class TextUtil
{
    /// <summary>Strips tags, decodes entities, collapses whitespace. Null when nothing is left.</summary>
    public static string? CleanHtml(string? html, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length == 0) return null;
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
}
