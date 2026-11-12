using Microsoft.Playwright;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>
/// <see cref="IFormFiller"/> on top of Playwright: one isolated browser context per run, open the page, hand it to the first
/// adapter that matches (or to the generic engine), always close the context. The run is aborted when the token is cancelled
/// (the caller's MaxRunSeconds timeout) by closing the context under the running operation.
/// </summary>
public sealed class PlaywrightFormFiller(
    PlaywrightHost host,
    IEnumerable<IAtsAdapter> adapters,
    ILogger<PlaywrightFormFiller> log) : IFormFiller
{
    private const float NavigationTimeoutMs = 30_000;
    private const float SettleTimeoutMs = 8_000;

    public async Task<FillReport> FillAsync(FillRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("FillRequest.Url must be an absolute http(s) URL.", nameof(request));

        var matcher = new FieldMatcher(request.StandardValues, request.CustomRules);

        var context = await host.NewContextAsync(ct);
        using var abort = ct.Register(() => _ = CloseQuietlyAsync(context));
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(request.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DomContentLoaded,
                Timeout = NavigationTimeoutMs
            });

            // Forms are usually drawn by script after the first paint; give the page a moment to go quiet, but do not insist.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = SettleTimeoutMs }); }
            catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { }

            var finalUri = Uri.TryCreate(page.Url, UriKind.Absolute, out var u) ? u : uri;
            var adapter = adapters.FirstOrDefault(a => a.Matches(finalUri));
            log.LogInformation("Filling a form on {Host} (hint: {Ats}, adapter: {Adapter}).",
                finalUri.Host, request.Ats, adapter?.GetType().Name ?? "generic");

            var fill = new FillContext(request, matcher, log, ct);
            return adapter is null
                ? await FormFillEngine.RunAsync(page, fill)
                : await adapter.FillAsync(page, fill);
        }
        catch (PlaywrightException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);   // the context was closed under us because the run was cancelled
        }
        finally
        {
            await CloseQuietlyAsync(context);
        }
    }

    private static async Task CloseQuietlyAsync(IBrowserContext context)
    {
        try { await context.CloseAsync(); }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException) { /* already closed */ }
    }
}
