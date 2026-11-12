using System.Text.Json;
using Microsoft.Playwright;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>One form control found on the page. Holds no user data.</summary>
/// <param name="Index">Number the scanner stamped on the element (attribute <c>data-wg-idx</c>), valid until the next scan.</param>
/// <param name="Tag">input, textarea or select.</param>
/// <param name="HasValue">The control already holds a value (typed text, a chosen select option, a checked box, an attached file).</param>
/// <param name="IsCombobox">A custom dropdown built from a text input; typing into it does not choose an option, so it is never filled.</param>
/// <param name="Options">Visible option texts of a select, otherwise empty.</param>
public sealed record ScannedField(
    int Index,
    IFrame Frame,
    string Tag,
    FieldDescriptor Descriptor,
    bool HasValue,
    bool IsCombobox,
    IReadOnlyList<string> Options)
{
    public ILocator Locator => Frame.Locator($"[data-wg-idx='{Index}']").First;

    /// <summary>Identity of the field across scans (an index is not stable; label, name, id and type are).</summary>
    public string Key => string.Join('|',
        FieldMatcher.Normalize(Descriptor.Label), Descriptor.Name, Descriptor.Id, Descriptor.InputType ?? Tag);
}

/// <summary>
/// Lists the visible, enabled inputs, textareas, selects and file inputs of a page, including those inside iframes and open
/// shadow roots, with the text a person would read as the question (aria-labelledby, label[for], wrapping label).
/// File inputs are listed even when hidden: sites hide them behind a styled button. Frames of captcha widgets are skipped.
/// Scanning only reads, apart from stamping <c>data-wg-idx</c> on the elements it returns.
/// </summary>
public static class FieldScanner
{
    private const int MaxFields = 300;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Raw(
        int Index, string Tag, string Type, string? Label, string? Name, string? Id, string? Placeholder,
        string? AriaLabel, string? Autocomplete, bool Required, bool ReadOnly, bool HasValue, bool Combobox,
        string[]? Options);

    private const string Script = """
        () => {
            const SKIP = new Set(['hidden', 'submit', 'button', 'image', 'reset', 'password']);
            const clean = s => (s || '').replace(/\s+/g, ' ').trim();
            const out = [];
            let n = 0;

            const textOfLabel = label => {
                const c = label.cloneNode(true);
                c.querySelectorAll('input,select,textarea,button').forEach(x => x.remove());
                return clean(c.textContent);
            };
            const labelFor = el => {
                const root = el.getRootNode();
                const by = el.getAttribute('aria-labelledby');
                if (by) {
                    const t = by.split(/\s+/).map(id => {
                        const r = root && root.getElementById ? root.getElementById(id) : null;
                        return r ? clean(r.textContent) : '';
                    }).filter(Boolean).join(' ');
                    if (t) return t;
                }
                if (el.labels && el.labels.length) {
                    const t = Array.from(el.labels).map(textOfLabel).filter(Boolean).join(' ');
                    if (t) return t;
                }
                const wrap = el.closest('label');
                return wrap ? textOfLabel(wrap) : '';
            };
            const visible = el => {
                if (!el.getClientRects().length) return false;
                const s = getComputedStyle(el);
                return s.visibility !== 'hidden' && s.display !== 'none';
            };

            const visit = root => {
                root.querySelectorAll('input,textarea,select').forEach(el => {
                    el.removeAttribute('data-wg-idx');
                    const tag = el.tagName.toLowerCase();
                    const type = tag === 'input' ? (el.getAttribute('type') || 'text').toLowerCase() : tag;
                    if (tag === 'input' && SKIP.has(type)) return;
                    if (el.disabled) return;
                    if (type !== 'file' && !visible(el)) return;

                    const idx = n++;
                    el.setAttribute('data-wg-idx', String(idx));

                    let options = null;
                    let hasValue;
                    if (tag === 'select') {
                        options = Array.from(el.options).map(o => clean(o.textContent));
                        hasValue = el.value !== '';
                    } else if (type === 'checkbox' || type === 'radio') {
                        hasValue = el.checked;
                    } else if (type === 'file') {
                        hasValue = !!(el.files && el.files.length);
                    } else {
                        hasValue = (el.value || '') !== '';
                    }

                    out.push({
                        index: idx, tag, type,
                        label: labelFor(el),
                        name: el.getAttribute('name') || '',
                        id: el.id || '',
                        placeholder: el.getAttribute('placeholder') || '',
                        ariaLabel: clean(el.getAttribute('aria-label')),
                        autocomplete: el.getAttribute('autocomplete') || '',
                        required: !!el.required || el.getAttribute('aria-required') === 'true',
                        readOnly: !!el.readOnly,
                        hasValue,
                        combobox: el.getAttribute('role') === 'combobox' || el.getAttribute('aria-haspopup') === 'listbox',
                        options
                    });
                });
                root.querySelectorAll('*').forEach(h => { if (h.shadowRoot) visit(h.shadowRoot); });
            };

            visit(document);
            return JSON.stringify(out);
        }
        """;

    public static async Task<IReadOnlyList<ScannedField>> ScanAsync(IPage page, CancellationToken ct)
    {
        var result = new List<ScannedField>();

        foreach (var frame in page.Frames)
        {
            ct.ThrowIfCancellationRequested();
            if (CaptchaDetector.IsCaptchaUrl(frame.Url)) continue;

            Raw[] raw;
            try
            {
                var json = await frame.EvaluateAsync<string>(Script);
                raw = JsonSerializer.Deserialize<Raw[]>(json, Json) ?? [];
            }
            catch (Exception ex) when (ex is PlaywrightException or JsonException)
            {
                continue;   // detached or not scriptable frame: nothing to fill there
            }

            foreach (var r in raw)
            {
                if (result.Count >= MaxFields) return result;

                var descriptor = new FieldDescriptor(
                    Label: Blank(r.Label), Name: Blank(r.Name), Id: Blank(r.Id), Placeholder: Blank(r.Placeholder),
                    AriaLabel: Blank(r.AriaLabel), Autocomplete: Blank(r.Autocomplete),
                    InputType: r.Tag == "input" ? r.Type : null,
                    Required: r.Required, Disabled: false, ReadOnly: r.ReadOnly);

                result.Add(new ScannedField(r.Index, frame, r.Tag, descriptor, r.HasValue, r.Combobox, r.Options ?? []));
            }
        }
        return result;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
