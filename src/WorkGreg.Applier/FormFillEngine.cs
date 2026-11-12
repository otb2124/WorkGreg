using Microsoft.Playwright;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>Hooks an ATS adapter can give the generic fill. Everything is optional; the default is the plain generic behaviour.</summary>
public sealed record FormFillOptions
{
    /// <summary>Do not treat the invisible reCAPTCHA badge as a captcha (see <see cref="CaptchaDetector"/>).</summary>
    public bool IgnoreInvisibleRecaptcha { get; init; }

    /// <summary>Tried before the generic fill for every field. Returns true when it filled the field itself.</summary>
    public Func<ScannedField, FillContext, Task<bool>>? FillOverride { get; init; }

    /// <summary>Fields that are page machinery, not questions (never filled, never reported as unmatched).</summary>
    public Func<ScannedField, bool>? IsNoise { get; init; }
}

/// <summary>
/// The generic fill: scan the form, resolve a value for every field with <see cref="FieldMatcher"/>, enter it, and report.
/// Used directly for unknown sites and as the base for ATS adapters. It never clicks a button and never submits.
/// Logs carry labels, keys and counts only, never an entered value.
/// </summary>
public static class FormFillEngine
{
    private const int MaxPasses = 3;          // a filled field can reveal follow-up fields
    private const float FieldTimeoutMs = 5000;

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "email", "tel", "url", "search", "number", "textarea"
    };

    public static async Task<FillReport> RunAsync(IPage page, FillContext ctx, FormFillOptions? options = null)
    {
        options ??= new FormFillOptions();
        var isNoise = options.IsNoise ?? (_ => false);

        // A captcha means a person has to take over: look, report, touch nothing.
        if (await CaptchaDetector.DetectAsync(page, options.IgnoreInvisibleRecaptcha))
        {
            ctx.Logger.LogInformation("Captcha detected; nothing was filled.");
            return new FillReport([], [], CaptchaDetected: true, Fingerprint: "", await ScreenshotAsync(page, ctx.Logger));
        }

        var filled = new List<string>();
        var handled = new HashSet<string>();
        string? fingerprint = null;

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var fields = (await FieldScanner.ScanAsync(page, ctx.Ct)).Where(f => !isNoise(f)).ToList();
            fingerprint ??= FormFingerprint.Compute(fields.Select(f => f.Descriptor.Label));

            var progress = false;
            foreach (var field in fields)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                if (handled.Contains(field.Key)) continue;

                if (await TryFillAsync(field, ctx, options))
                {
                    handled.Add(field.Key);
                    filled.Add(DisplayLabel(field.Descriptor));
                    progress = true;
                }
            }
            if (!progress) break;
        }

        // Look again: required fields that are still empty are what the user has to answer.
        var finalFields = (await FieldScanner.ScanAsync(page, ctx.Ct)).Where(f => !isNoise(f)).ToList();
        var unmatched = UnmatchedRequired(finalFields, handled);

        ctx.Logger.LogInformation("Fill finished: {Filled} filled, {Unmatched} required fields unmatched.",
            filled.Count, unmatched.Count);

        return new FillReport(filled, unmatched, CaptchaDetected: false, fingerprint ?? "", await ScreenshotAsync(page, ctx.Logger));
    }

    /// <summary>Required, still empty, not filled by us. One entry per radio group (or per field when there is no name).</summary>
    internal static List<UnmatchedField> UnmatchedRequired(IEnumerable<ScannedField> fields, ISet<string> handled)
    {
        var result = new List<UnmatchedField>();
        var seen = new HashSet<string>();

        foreach (var f in fields)
        {
            var d = f.Descriptor;
            if (!d.Required || d.ReadOnly || f.HasValue || handled.Contains(f.Key)) continue;

            var groupKey = d.InputType is "radio" && d.Name is not null ? "radio:" + d.Name : f.Key;
            if (!seen.Add(groupKey)) continue;

            result.Add(new UnmatchedField(DisplayLabel(d), d.Id ?? d.Name ?? ""));
        }
        return result;
    }

    private static async Task<bool> TryFillAsync(ScannedField field, FillContext ctx, FormFillOptions options)
    {
        var d = field.Descriptor;
        if (FieldMatcher.IsSkipped(d)) return false;
        if (options.FillOverride is { } custom && await custom(field, ctx)) return true;
        if (field.IsCombobox) return false;

        try
        {
            if (d.InputType == "file")
            {
                var cv = ctx.Request.Cv;
                if (cv is null || field.HasValue || !IsCvField(d)) return false;

                await field.Locator.SetInputFilesAsync(
                    new FilePayload { Name = cv.FileName, MimeType = cv.ContentType, Buffer = cv.Content },
                    new LocatorSetInputFilesOptions { Timeout = FieldTimeoutMs });
                return true;
            }

            if (d.InputType is "checkbox" or "radio") return false;   // choices are the user's, never guessed

            var isSelect = field.Tag == "select";
            if (!isSelect && !TextTypes.Contains(d.InputType ?? field.Tag)) return false;

            var match = ctx.Matcher.Match(d);
            if (match is null) return false;

            var value = ResolveValue(match, ctx.Request);

            if (isSelect)
            {
                var option = FindOption(field.Options, value);
                if (option is null) return false;
                await field.Locator.SelectOptionAsync(
                    new SelectOptionValue { Label = option },
                    new LocatorSelectOptionOptions { Timeout = FieldTimeoutMs });
                return true;
            }

            await field.Locator.FillAsync(value, new LocatorFillOptions { Timeout = FieldTimeoutMs });
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException)
        {
            // Type only: a Playwright message can echo selectors, and we never log values.
            ctx.Logger.LogWarning("Could not fill '{Label}' ({Error}).", DisplayLabel(d), ex.GetType().Name);
            return false;
        }
    }

    internal static string ResolveValue(MatchResult match, FillRequest request)
    {
        if (match.Key != AutofillKey.CoverLetter) return match.Value;

        request.StandardValues.TryGetValue(AutofillKey.FirstName, out var first);
        request.StandardValues.TryGetValue(AutofillKey.LastName, out var last);
        return CoverLetterTemplate.Expand(match.Value, first, last, request.Job.Company, request.Job.Title);
    }

    /// <summary>The option whose text equals the value (normalized), else the only option containing it as whole words.</summary>
    internal static string? FindOption(IReadOnlyList<string> options, string value)
    {
        var wanted = FieldMatcher.Normalize(value);
        if (wanted.Length == 0) return null;

        foreach (var option in options)
            if (FieldMatcher.Normalize(option) == wanted) return option;

        var padded = " " + wanted + " ";
        var containing = options
            .Where(o => (" " + FieldMatcher.Normalize(o) + " ").Contains(padded, StringComparison.Ordinal))
            .ToList();
        return containing.Count == 1 ? containing[0] : null;
    }

    /// <summary>A file input that asks for the CV / resume, and not for a cover letter.</summary>
    internal static bool IsCvField(FieldDescriptor d)
    {
        var text = " " + string.Join(' ',
            new[] { d.Label, d.AriaLabel, d.Name, d.Id }.Select(FieldMatcher.Normalize).Where(t => t.Length > 0)) + " ";

        if (text.Contains(" cover letter ", StringComparison.Ordinal) ||
            text.Contains(" motivacijsko pismo ", StringComparison.Ordinal)) return false;

        return text.Contains(" resume ", StringComparison.Ordinal) ||
               text.Contains(" cv ", StringComparison.Ordinal) ||
               text.Contains(" curriculum vitae ", StringComparison.Ordinal) ||
               text.Contains(" zivotopis ", StringComparison.Ordinal);
    }

    internal static string DisplayLabel(FieldDescriptor d)
    {
        var text = d.Label ?? d.AriaLabel ?? d.Placeholder ?? d.Name ?? d.Id;
        text = text?.Trim().TrimEnd('*', ' ');
        return string.IsNullOrEmpty(text) ? "(unlabeled field)" : text;
    }

    public static async Task<byte[]?> ScreenshotAsync(IPage page, ILogger log)
    {
        try
        {
            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = true,
                Type = ScreenshotType.Jpeg,
                Quality = 80
            });
        }
        catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException)
        {
            log.LogWarning("Could not take a screenshot ({Error}).", ex.GetType().Name);
            return null;
        }
    }
}