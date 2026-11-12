using System.Text.RegularExpressions;

namespace WorkGreg.Applying;

/// <summary>
/// Expands the tokens {{Company}}, {{Title}}, {{FirstName}} and {{LastName}} in a stored cover-letter template.
/// Token names are case-insensitive; an unknown token is left as typed so the user can spot it.
/// </summary>
public static class CoverLetterTemplate
{
    private static readonly Regex Token = new(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static string Expand(string template, string? firstName, string? lastName, string? company, string? title)
    {
        if (string.IsNullOrEmpty(template)) return "";

        return Token.Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "company" => company?.Trim() ?? "",
            "title" => title?.Trim() ?? "",
            "firstname" => firstName?.Trim() ?? "",
            "lastname" => lastName?.Trim() ?? "",
            _ => m.Value
        });
    }
}
