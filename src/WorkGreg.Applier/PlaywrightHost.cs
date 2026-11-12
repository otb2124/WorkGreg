using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WorkGreg.Services;

namespace WorkGreg.Applying;

/// <summary>Chromium is missing on this machine. The message says how to install it.</summary>
public sealed class BrowserNotInstalledException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Owns the one Chromium process. Started on first use (nothing is launched while Applier is idle or disabled), reused by
/// every run, closed on shutdown. Each run gets its own <see cref="IBrowserContext"/>, so cookies and storage never leak
/// from one application to the next. The caller disposes the context.
/// </summary>
public sealed class PlaywrightHost(IOptions<ApplierOptions> options, ILogger<PlaywrightHost> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    public async Task<IBrowserContext> NewContextAsync(CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            Locale = "en-US",
            AcceptDownloads = false
        });
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_browser is { IsConnected: true }) return _browser;

            await CloseBrowserAsync();   // a crashed or disconnected browser is replaced

            _playwright ??= await Playwright.CreateAsync();
            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = options.Value.Headless
                });
            }
            catch (PlaywrightException ex) when (IsMissingBrowser(ex))
            {
                throw new BrowserNotInstalledException(InstallHelp, ex);
            }

            log.LogInformation("Chromium started (headless: {Headless}).", options.Value.Headless);
            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal const string InstallHelp =
        "Chromium is not installed for Playwright, so the autofill browser cannot start. Install it once on this machine: " +
        "run `pwsh playwright.ps1 install chromium` from the Host's build output folder (on Linux add `--with-deps`), " +
        "or `playwright install chromium` if the Playwright CLI is installed. Then restart the app.";

    internal static bool IsMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);

    private async Task CloseBrowserAsync()
    {
        var browser = _browser;
        _browser = null;
        if (browser is null) return;
        try { await browser.CloseAsync(); }
        catch (PlaywrightException) { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseBrowserAsync();
            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
