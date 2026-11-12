using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkGreg.Domain;

public enum MatchSource { Custom = 0, Autocomplete = 1, Synonym = 2 }

/// <summary>What the matcher knows about one form field. Filled in by the browser side (FieldScanner); nothing here is user data.</summary>
public sealed record FieldDescriptor(
    string? Label = null,
    string? Name = null,
    string? Id = null,
    string? Placeholder = null,
    string? AriaLabel = null,
    string? Autocomplete = null,
    string? InputType = null,
    bool Required = false,
    bool Disabled = false,
    bool ReadOnly = false);

/// <summary>A custom "field pattern -> answer" rule. A plain copy of <see cref="AutofillField"/> so the matcher stays free of EF types.</summary>
public sealed record CustomRule(int Id, FieldMatchBy MatchBy, string Pattern, bool IsRegex, string Value, int SortOrder = 0);

/// <param name="Value">What to enter. For <see cref="AutofillKey.CoverLetter"/> this is the raw template; token expansion is the caller's job.</param>
/// <param name="RuleId">Custom matches only.</param>
/// <param name="Key">Standard matches only; null for custom rules and for the derived full name.</param>
public sealed record MatchResult(string Value, MatchSource Source, int? RuleId, AutofillKey? Key);

/// <summary>
/// Decides which stored value (if any) belongs into one form field. Pure and dependency-free (no EF, no browser, no logging).
/// Order: custom rules (by SortOrder) -> autocomplete attribute -> built-in English/Croatian synonyms -> null.
/// Demographic / equal-opportunity fields are never filled by the last two steps; only an explicit custom rule fills them.
/// </summary>
public sealed class FieldMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> SkippedInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "hidden", "submit"
    };

    private static readonly Dictionary<string, AutofillKey> AutocompleteKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["given-name"] = AutofillKey.FirstName,
        ["family-name"] = AutofillKey.LastName,
        ["email"] = AutofillKey.Email,
        ["tel"] = AutofillKey.Phone,
        ["address-level2"] = AutofillKey.City,
        ["country"] = AutofillKey.Country,
        ["country-name"] = AutofillKey.Country
    };

    /// <param name="Key">Null = the derived full name (first + last).</param>
    /// <param name="Phrases">Match by equality or as whole words inside a longer text.</param>
    /// <param name="ExactOnly">Match by equality only, because as words they are too common ("name" is in "company name").</param>
    private sealed record SynonymGroup(AutofillKey? Key, string[] Phrases, string[] ExactOnly);

    // Order matters for the "contains" pass: the full-name phrases must be tried before "ime" / "prezime" ("ime i prezime" contains both).
    private static readonly SynonymGroup[] Groups =
    [
        Group(null, ["full name", "ime i prezime"], ["name"]),
        Group(AutofillKey.FirstName, ["first name", "given name", "ime"]),
        Group(AutofillKey.LastName, ["last name", "family name", "surname", "prezime"]),
        Group(AutofillKey.Email, ["email", "e mail", "email address", "e mail adresa"]),
        Group(AutofillKey.Phone, ["phone", "mobile", "telephone", "telefon", "mobitel", "broj telefona"]),
        Group(AutofillKey.City, ["city", "grad"]),
        Group(AutofillKey.Country, ["country", "drzava", "zemlja"]),
        Group(AutofillKey.LinkedIn, ["linkedin"]),
        Group(AutofillKey.GitHub, ["github"]),
        Group(AutofillKey.Website, ["website", "portfolio", "personal site", "web stranica"]),
        Group(AutofillKey.Salary, ["salary", "expected salary", "compensation", "placa", "ocekivana placa"]),
        Group(AutofillKey.NoticePeriod, ["notice period", "otkazni rok"]),
        Group(AutofillKey.CoverLetter, ["cover letter", "motivacijsko pismo"])
    ];

    private static SynonymGroup Group(AutofillKey? key, string[] phrases, string[]? exactOnly = null) =>
        new(key, phrases.Select(Normalize).ToArray(), (exactOnly ?? []).Select(Normalize).ToArray());

    /// <summary>Whole words / phrases (already normalized) that mark a demographic or equal-opportunity question.</summary>
    private static readonly string[] DemographicTerms = new[]
    {
        "gender", "gender identity", "sex", "sexual orientation", "orientation", "transgender", "pronoun", "pronouns", "lgbt", "lgbtq",
        "race", "ethnicity", "ethnic", "ethnic origin", "hispanic", "latino", "latina",
        "disability", "disabled", "veteran", "religion",
        "spol", "rasa", "invaliditet", "etnicka pripadnost", "etnicko porijeklo", "vjera", "religija"
    }.Select(Normalize).ToArray();

    private readonly Dictionary<AutofillKey, string> _values = new();
    private readonly List<Rule> _rules;

    /// <param name="values">Standard values by key. Blank values count as "not set".</param>
    /// <param name="rules">Custom rules; evaluated by SortOrder, then Id.</param>
    public FieldMatcher(IReadOnlyDictionary<AutofillKey, string>? values, IEnumerable<CustomRule>? rules = null)
    {
        if (values is not null)
            foreach (var (key, value) in values)
                if (!string.IsNullOrWhiteSpace(value)) _values[key] = value.Trim();

        _rules = (rules ?? [])
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .Select(r => new Rule(r))
            .Where(r => r.Usable)
            .ToList();
    }

    /// <summary>The value for this field, or null when nothing should be entered.</summary>
    public MatchResult? Match(FieldDescriptor field)
    {
        if (IsSkipped(field)) return null;

        // 1. Custom rules: the user's explicit choice wins over everything, including the demographic block below.
        foreach (var rule in _rules)
            if (rule.Matches(field))
                return new MatchResult(rule.Value, MatchSource.Custom, rule.Id, null);

        // Built-ins never touch demographic questions.
        if (IsDemographic(field)) return null;

        // 2. autocomplete attribute (may hold several tokens, e.g. "section-a shipping given-name").
        if (!string.IsNullOrWhiteSpace(field.Autocomplete))
        {
            foreach (var token in field.Autocomplete.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (AutocompleteKeys.TryGetValue(token, out var key))
                    return FromKey(key, MatchSource.Autocomplete);
        }

        // 3. Synonyms. Human-readable text (label, aria-label, placeholder) is trusted first. The technical name/id are only a
        // fallback, and without the exact-only "name" synonym, so a field labelled "Company name" is never filled with a person's name.
        var texts = new[] { field.Label, field.AriaLabel, field.Placeholder }
            .Select(Normalize).Where(t => t.Length > 0).ToList();
        var attributes = new[] { field.Name, field.Id }
            .Select(Normalize).Where(t => t.Length > 0).ToList();

        var group = FindGroup(texts, allowExactOnly: true) ?? FindGroup(attributes, allowExactOnly: false);
        return group is null ? null : FromKey(group.Key, MatchSource.Synonym);
    }

    /// <summary>Password, hidden and submit inputs, and disabled or read-only fields, are never filled.</summary>
    public static bool IsSkipped(FieldDescriptor field) =>
        field.Disabled || field.ReadOnly ||
        (field.InputType is { } type && SkippedInputTypes.Contains(type.Trim()));

    /// <summary>Gender, race, ethnicity, disability, veteran status, sexual orientation and similar equal-opportunity questions.</summary>
    public static bool IsDemographic(FieldDescriptor field)
    {
        var text = " " + string.Join(' ',
            new[] { field.Label, field.AriaLabel, field.Placeholder, field.Name, field.Id, field.Autocomplete }
                .Select(Normalize).Where(t => t.Length > 0)) + " ";
        return DemographicTerms.Any(term => text.Contains(" " + term + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Unicode NFD, strip combining marks, đ -> d, lowercase, every run of non-alphanumerics becomes one space, trimmed.
    /// "Očekivana plaća!" -> "ocekivana placa".
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string decomposed;
        try { decomposed = text.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = text; }   // invalid Unicode (lone surrogate): use it as is

        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            var c = raw is 'đ' or 'Đ' ? 'd' : raw;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    private static SynonymGroup? FindGroup(List<string> parts, bool allowExactOnly)
    {
        // Equality beats "contains" across all parts, so a precise label is not overruled by a vague one.
        foreach (var part in parts)
            foreach (var group in Groups)
                if (group.Phrases.Contains(part) || (allowExactOnly && group.ExactOnly.Contains(part)))
                    return group;

        foreach (var part in parts)
        {
            var padded = " " + part + " ";
            foreach (var group in Groups)
                if (group.Phrases.Any(phrase => padded.Contains(" " + phrase + " ", StringComparison.Ordinal)))
                    return group;
        }
        return null;
    }

    /// <summary>A recognised field whose value the user has not set gives null: we do not guess another field's value.</summary>
    private MatchResult? FromKey(AutofillKey? key, MatchSource source)
    {
        if (key is null)
        {
            return _values.TryGetValue(AutofillKey.FirstName, out var first) && _values.TryGetValue(AutofillKey.LastName, out var last)
                ? new MatchResult($"{first} {last}", source, null, null)
                : null;
        }
        return _values.TryGetValue(key.Value, out var value) ? new MatchResult(value, source, null, key) : null;
    }

    private sealed class Rule
    {
        public int Id { get; }
        public FieldMatchBy MatchBy { get; }
        public string Value { get; } = "";
        public string PlainPattern { get; } = "";
        public Regex? Compiled { get; }
        public bool Usable { get; }

        public Rule(CustomRule source)
        {
            Id = source.Id;
            MatchBy = source.MatchBy;
            Value = (source.Value ?? "").Trim();
            var pattern = source.Pattern ?? "";
            if (Value.Length == 0 || pattern.Trim().Length == 0) return;   // nothing to match or nothing to enter

            if (source.IsRegex)
            {
                try
                {
                    Compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                    Usable = true;
                }
                catch (ArgumentException) { /* invalid regex: the rule never matches */ }
            }
            else
            {
                PlainPattern = Normalize(pattern);
                Usable = PlainPattern.Length > 0;
            }
        }

        public bool Matches(FieldDescriptor field)
        {
            foreach (var part in Parts(field, MatchBy))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;

                if (Compiled is not null)
                {
                    try
                    {
                        // The raw text first (so "^why .*us\?" works), then the normalized text (so "drzava" finds "Država").
                        if (Compiled.IsMatch(part) || Compiled.IsMatch(Normalize(part))) return true;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;   // a runaway pattern just does not match
                    }
                }
                else if (Normalize(part).Contains(PlainPattern, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // Label also covers aria-label: both are the human-readable question.
        private static IEnumerable<string?> Parts(FieldDescriptor f, FieldMatchBy by) => by switch
        {
            FieldMatchBy.Label => [f.Label, f.AriaLabel],
            FieldMatchBy.Name => [f.Name],
            FieldMatchBy.Id => [f.Id],
            FieldMatchBy.Placeholder => [f.Placeholder],
            _ => [f.Label, f.AriaLabel, f.Placeholder, f.Name, f.Id]
        };
    }
}