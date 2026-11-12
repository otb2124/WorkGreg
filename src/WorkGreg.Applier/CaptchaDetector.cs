using Microsoft.Playwright;

namespace WorkGreg.Applying;

/// <summary>
/// Finds captcha widgets: frames whose URL names reCAPTCHA / hCaptcha / Turnstile, iframes whose src does, or any element with
/// <c>data-sitekey</c>. Detection only: this code never clicks or types into a captcha, and the caller stops filling.
/// A site that loads an invisible captcha script on every page is reported as captcha too (deliberately cautious).
/// </summary>
public static class CaptchaDetector
{
    private static readonly string[] Markers = ["recaptcha", "hcaptcha", "turnstile"];

    private const string Script = """
        () => {
            const hit = s => /recaptcha|hcaptcha|turnstile/i.test(s || '');
            if (document.querySelector('[data-sitekey]')) return true;
            return Array.from(document.querySelectorAll('iframe')).some(f => hit(f.getAttribute('src')));
        }
        """;

    internal static bool IsCaptchaUrl(string? url) =>
        !string.IsNullOrEmpty(url) && Markers.Any(m => url.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static async Task<bool> DetectAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            if (IsCaptchaUrl(frame.Url)) return true;
            try
            {
                if (await frame.EvaluateAsync<bool>(Script)) return true;
            }
            catch (PlaywrightException)
            {
                // the frame went away while we looked: nothing to see there
            }
        }
        return false;
    }
}
