using Microsoft.Playwright;

namespace WorkGreg.Applying;

/// <summary>
/// Finds captcha widgets: frames whose URL names reCAPTCHA / hCaptcha / Turnstile, iframes whose src does, or any element with
/// <c>data-sitekey</c>. Detection only: this code never clicks or types into a captcha, and the caller stops filling.
/// A site that loads an invisible captcha script on every page is reported as captcha too (deliberately cautious),
/// unless the caller opts out with <c>ignoreInvisibleRecaptcha</c> (Greenhouse puts one on every form, see GreenhouseAdapter).
/// Only the invisible reCAPTCHA badge (anchor URL with <c>size=invisible</c>) can be ignored; a visible widget, a challenge
/// frame, hCaptcha and Turnstile always count.
/// </summary>
public static class CaptchaDetector
{
    private static readonly string[] Markers = ["recaptcha", "hcaptcha", "turnstile"];

    private const string Script = """
        (ignoreInvisible) => {
            const hit = s => /recaptcha|hcaptcha|turnstile/i.test(s || '');
            const invisible = s => /recaptcha/i.test(s || '') && /[?&]size=invisible/i.test(s || '');
            const keyed = Array.from(document.querySelectorAll('[data-sitekey]'));
            if (keyed.some(e => !(ignoreInvisible && e.getAttribute('data-size') === 'invisible'))) return true;
            return Array.from(document.querySelectorAll('iframe')).some(f => {
                const src = f.getAttribute('src');
                return hit(src) && !(ignoreInvisible && invisible(src));
            });
        }
        """;

    internal static bool IsCaptchaUrl(string? url) =>
        !string.IsNullOrEmpty(url) && Markers.Any(m => url.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The badge frame of an invisible reCAPTCHA: it shows nothing and only acts when the form is submitted.</summary>
    internal static bool IsInvisibleRecaptchaUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        url.Contains("recaptcha", StringComparison.OrdinalIgnoreCase) &&
        (url.Contains("?size=invisible", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("&size=invisible", StringComparison.OrdinalIgnoreCase));

    public static async Task<bool> DetectAsync(IPage page, bool ignoreInvisibleRecaptcha = false)
    {
        foreach (var frame in page.Frames)
        {
            if (IsCaptchaUrl(frame.Url) && !(ignoreInvisibleRecaptcha && IsInvisibleRecaptchaUrl(frame.Url))) return true;
            try
            {
                if (await frame.EvaluateAsync<bool>(Script, ignoreInvisibleRecaptcha)) return true;
            }
            catch (PlaywrightException)
            {
                // the frame went away while we looked: nothing to see there
            }
        }
        return false;
    }
}