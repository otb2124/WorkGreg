using Microsoft.Playwright;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>
/// Greenhouse's new job-board form (job-boards.greenhouse.io, React). Built against saved pages of two real forms; what the
/// fixtures show and this adapter relies on:
/// <list type="bullet">
/// <item>Text questions (<c>question_&lt;id&gt;</c>) are plain labelled inputs: the generic fill already handles them.</item>
/// <item>Dropdowns are react-select comboboxes (<c>role="combobox"</c> text input, placeholder <c>react-select-&lt;id&gt;-placeholder</c>).
/// The generic fill skips comboboxes, so this adapter opens the menu and clicks the option.</item>
/// <item>Every required react-select has a hidden, label-less <c>required</c> shim input. It is page machinery, not a question.</item>
/// <item>The resume is a hidden <c>input#resume[type=file]</c> labelled only "Attach"; the real name sits on its group
/// (<c>aria-labelledby="upload-label-resume"</c>). Same for checkboxes: the question is the fieldset legend.</item>
/// <item>An invisible reCAPTCHA Enterprise badge is on every form, so it must not count as a captcha here.</item>
/// <item>Demographic questions (gender, Hispanic/Latino, veteran status...) stay blank: <see cref="FieldMatcher"/> only fills
/// them from an explicit custom rule.</item>
/// </list>
/// It never clicks the submit button, never presses Enter, and blocks the form's submit event as a second line of defence.
/// </summary>
public sealed class GreenhouseAdapter : IAtsAdapter
{
    // react-select renders options with role="option" while the menu is open. The phone widget (intl-tel-input) also uses
    // role="option" for its country list, always in the DOM, so its ids (iti-...) are excluded.
    private const string OptionSelector = "[role='option']:not([id^='iti-']):visible";

    private const float ActionTimeoutMs = 4000;
    private const float MenuWaitMs = 1500;        // a static menu is there right after the click
    private const float SearchWaitMs = 2500;      // async lists (location search) need time after typing

    public bool Matches(Uri url) => AtsDetector.Detect(url.AbsoluteUri) == AtsKind.Greenhouse;

    public Task<FillReport> FillAsync(IPage page, FillContext ctx) => FillCoreAsync(page, ctx, guardSubmit: true);

    /// <param name="guardSubmit">True for every dry run. The Phase 4 submit path must pass false, or its click would be blocked too.</param>
    internal async Task<FillReport> FillCoreAsync(IPage page, FillContext ctx, bool guardSubmit)
    {
        if (guardSubmit) await GuardSubmitAsync(page, ctx.Logger);

        var attempted = new HashSet<string>();   // per run: a dropdown that failed once is not retried in the next pass
        var options = new FormFillOptions
        {
            IgnoreInvisibleRecaptcha = true,
            IsNoise = IsUnlabeledShim,
            FillOverride = (field, c) => TryFillDropdownAsync(field, c, attempted)
        };

        var report = await FormFillEngine.RunAsync(page, ctx, options);
        return report.UnmatchedRequired.Count == 0
            ? report
            : report with { UnmatchedRequired = await NameGroupsAsync(page, report.UnmatchedRequired) };
    }

    /// <summary>No label, aria-label, placeholder, name or id at all: nothing a person could answer (react-select's required shim).</summary>
    internal static bool IsUnlabeledShim(ScannedField f)
    {
        var d = f.Descriptor;
        return f.Tag == "input" && d.InputType is "text" or null &&
               d.Label is null && d.AriaLabel is null && d.Placeholder is null && d.Name is null && d.Id is null;
    }

    // ---------------------------------------------------------------- dropdowns

    private static async Task<bool> TryFillDropdownAsync(ScannedField field, FillContext ctx, HashSet<string> attempted)
    {
        if (!field.IsCombobox) return false;
        var d = field.Descriptor;
        if (!attempted.Add(field.Key)) return false;

        var match = ctx.Matcher.Match(d);   // custom rule, autocomplete or synonym; demographic fields only via a custom rule
        if (match is null) return false;
        var value = FormFillEngine.ResolveValue(match, ctx.Request);

        var label = FormFillEngine.DisplayLabel(d);
        var input = field.Locator;
        var options = field.Frame.Locator(OptionSelector);
        var placeholder = d.Id is null ? null : field.Frame.Locator($"[id='react-select-{d.Id.Replace("'", "\\'")}-placeholder']");

        try
        {
            var hadPlaceholder = placeholder is not null && await placeholder.CountAsync() > 0;

            await input.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });   // opens the menu; no keys are pressed
            var picked = await PickOptionAsync(options, value, MenuWaitMs, ctx.Ct);

            if (!picked)
            {
                // Long or async lists show their options only after typing. Typing never submits; Enter is never pressed.
                await input.FillAsync(value, new LocatorFillOptions { Timeout = ActionTimeoutMs });
                picked = await PickOptionAsync(options, value, SearchWaitMs, ctx.Ct);
            }

            if (picked && hadPlaceholder)
            {
                // The placeholder goes away once a value is chosen.
                try { await placeholder!.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = MenuWaitMs }); }
                catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { picked = false; }
            }

            if (!picked) await BackOutAsync(input);
            else ctx.Logger.LogInformation("Dropdown '{Label}' filled.", label);
            return picked;
        }
        catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException)
        {
            ctx.Logger.LogWarning("Could not fill dropdown '{Label}' ({Error}).", label, ex.GetType().Name);
            await BackOutAsync(input);
            return false;
        }
    }

    /// <summary>Waits for the open menu, matches the wanted text with the engine's option rule, clicks that option.</summary>
    private static async Task<bool> PickOptionAsync(ILocator options, string value, float waitMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await options.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = waitMs }); }
        catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { return false; }

        var texts = (await options.AllInnerTextsAsync()).Select(t => t.Trim()).ToList();
        var chosen = FormFillEngine.FindOption(texts, value);
        if (chosen is null) return false;

        await options.Nth(texts.IndexOf(chosen)).ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
        return true;
    }

    /// <summary>Leave the dropdown as we found it: no typed search text, menu closed.</summary>
    private static async Task BackOutAsync(ILocator input)
    {
        try
        {
            await input.FillAsync("", new LocatorFillOptions { Timeout = 1000 });
            await input.PressAsync("Escape", new LocatorPressOptions { Timeout = 1000 });
        }
        catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { /* best effort */ }
    }

    // ---------------------------------------------------------------- report and safety

    /// <summary>
    /// A file input is labelled "Attach" and a consent checkbox carries a paragraph of legal text; the question a person reads
    /// is the group's label ("Resume/CV", "GDPR consent"). Only file inputs, checkboxes and radios are renamed, so a
    /// text field inside the Phone fieldset is never reported as "Phone".
    /// </summary>
    private static async Task<IReadOnlyList<UnmatchedField>> NameGroupsAsync(IPage page, IReadOnlyList<UnmatchedField> unmatched)
    {
        string?[] names;
        try
        {
            names = await page.EvaluateAsync<string?[]>("""
                (ids) => ids.map(id => {
                    const el = id ? document.getElementById(id) : null;
                    if (!el || !['file', 'checkbox', 'radio'].includes(el.type)) return null;
                    const clean = s => (s || '').replace(/\s+/g, ' ').replace(/[\s*]+$/, '').trim();
                    const group = el.closest('[role="group"][aria-labelledby], fieldset');
                    if (!group) return null;
                    const by = group.getAttribute('aria-labelledby');
                    const named = by ? document.getElementById(by.split(/\s+/)[0]) : null;
                    const text = named ? clean(named.textContent)
                        : clean(group.querySelector('legend')?.textContent);
                    return text || null;
                })
                """, unmatched.Select(u => u.Id).ToArray());
        }
        catch (PlaywrightException)
        {
            return unmatched;
        }

        var result = new List<UnmatchedField>();
        var seen = new HashSet<string>();
        for (var i = 0; i < unmatched.Count; i++)
        {
            var label = i < names.Length && !string.IsNullOrWhiteSpace(names[i]) ? names[i]! : unmatched[i].Label;
            if (seen.Add(label)) result.Add(unmatched[i] with { Label = label });   // a checkbox group lists each box: one entry is enough
        }
        return result;
    }

    /// <summary>Stops every form on the page from submitting (capture phase, before the page's own handlers).</summary>
    private static async Task GuardSubmitAsync(IPage page, ILogger log)
    {
        var guarded = 0;
        foreach (var frame in page.Frames)
        {
            try
            {
                guarded += await frame.EvaluateAsync<int>("""
                    () => {
                        const forms = Array.from(document.querySelectorAll('form'));
                        forms.forEach(f => f.addEventListener('submit', e => { e.preventDefault(); e.stopImmediatePropagation(); }, true));
                        return forms.length;
                    }
                    """);
            }
            catch (PlaywrightException) { /* frame went away */ }
        }
        log.LogInformation("Submit blocked on {Forms} form(s) for this dry run.", guarded);
    }
}