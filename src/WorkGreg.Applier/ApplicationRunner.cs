using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Services;

namespace WorkGreg.Applying;

/// <summary>
/// Runs queued applications through <see cref="IFormFiller"/>, strictly one at a time. One run = claim the row (Queued -> Filling),
/// fill the form in the browser under a <c>MaxRunSeconds</c> timeout, store the report and screenshot, move to Ready (all
/// required fields filled), NeedsInput (a required field or a captcha needs the user) or Failed. It never submits.
/// Requests to the same host are spaced by <see cref="HostThrottle"/>. On startup a row left in Filling becomes Failed
/// ("interrupted") and rows still Queued are queued again, so a restart leaves nothing stuck. When <c>Applier:Enabled</c> is
/// false it does nothing and <see cref="EnqueueAsync"/> answers <see cref="EnqueueResult.Disabled"/>.
/// Logs carry ids, statuses, labels' counts and error type names, never entered values or exception messages.
/// </summary>
public sealed class ApplicationRunner(
    IServiceScopeFactory scopes,
    IFormFiller filler,
    HostThrottle throttle,
    IOptions<ApplierOptions> options,
    ILogger<ApplicationRunner> log) : BackgroundService, IApplicationQueue
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public async Task<EnqueueResult> EnqueueAsync(long chatId, int applicationId, CancellationToken ct)
    {
        if (!options.Value.Enabled) return EnqueueResult.Disabled;

        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ApplicationRunStore>();

        if (await store.CountRunsTodayAsync(chatId, applicationId, ct) >= options.Value.MaxPerDay)
            return EnqueueResult.DailyLimitReached;

        _queue.Writer.TryWrite(applicationId);   // unbounded: always succeeds. A duplicate id is harmless, the claim below lets one run win.
        return EnqueueResult.Queued;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Applier is off (Applier:Enabled is false): autofill stays a preview.");
            return;
        }

        await RecoverAsync(ct);

        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await RunOneAsync(id, ct);
            }
            // Only a real shutdown may end the worker; one bad run must not stop the next ones.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError("Application {Id} crashed ({Error}).", id, ex.GetType().Name);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ApplicationRunStore>();

            var interrupted = await store.FailInterruptedAsync(ct);
            var queued = await store.QueuedIdsAsync(ct);
            foreach (var id in queued) _queue.Writer.TryWrite(id);

            log.LogInformation("Applier started: {Interrupted} interrupted run(s) marked failed, {Queued} queued run(s) restored.",
                interrupted, queued.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError("Applier recovery failed ({Error}).", ex.GetType().Name);
        }
    }

    private async Task RunOneAsync(int id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ApplicationRunStore>();

        var info = await store.LoadAsync(id, ct);
        if (info is null || !await store.TryStartAsync(id, ct))
        {
            log.LogInformation("Application {Id} not run: it is gone or no longer queued.", id);
            return;
        }

        var ats = AtsDetector.Detect(info.Url);
        var input = await store.BuildInputAsync(info, ats, ct);
        var request = input.Request;
        if (request is null)
        {
            await FinishAsync(store, id, ApplyStatus.Failed, ats, null, null, input.Problem, ct);
            return;
        }

        FillReport report;
        try
        {
            report = await throttle.RunAsync(info.Url, () => FillWithTimeoutAsync(request, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutting down: the row stays Filling and is failed as "interrupted" at the next start
        }
        catch (Exception ex)
        {
            await FinishAsync(store, id, ApplyStatus.Failed, ats, null, null, ErrorText(ex), ct);
            return;
        }

        var status = report.CaptchaDetected || report.UnmatchedRequired.Count > 0 ? ApplyStatus.NeedsInput : ApplyStatus.Ready;
        log.LogInformation("Application {Id}: {Filled} filled, {Unmatched} required unmatched, captcha: {Captcha}.",
            id, report.FilledLabels.Count, report.UnmatchedRequired.Count, report.CaptchaDetected);

        await FinishAsync(store, id, status, ats, JsonSerializer.Serialize(report), report.Screenshot, null, ct);
    }

    private Task<FillReport> FillWithTimeoutAsync(FillRequest request, CancellationToken ct)
    {
        // The timeout starts when the browser starts, not while the host throttle makes us wait.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value.MaxRunSeconds));
        return RunAndDisposeAsync(cts, request);

        async Task<FillReport> RunAndDisposeAsync(CancellationTokenSource source, FillRequest r)
        {
            try { return await filler.FillAsync(r, source.Token); }
            finally { source.Dispose(); }
        }
    }

    private async Task FinishAsync(
        ApplicationRunStore store, int id, ApplyStatus status, AtsKind ats,
        string? reportJson, byte[]? screenshot, string? error, CancellationToken ct)
    {
        if (!await store.TryFinishAsync(id, status, ats, reportJson, screenshot, error, ct))
        {
            log.LogInformation("Application {Id}: result dropped, its status changed during the run (cancelled?).", id);
            return;
        }
        if (status == ApplyStatus.Failed) log.LogWarning("Application {Id} failed: {Error}", id, error);
    }

    /// <summary>User-facing, fixed texts. An exception's own message is never stored or logged: it can echo page content.</summary>
    private string ErrorText(Exception ex) => ex switch
    {
        BrowserNotInstalledException b => b.Message,   // fixed install instructions
        OperationCanceledException => $"The run took longer than {options.Value.MaxRunSeconds} s and was stopped.",
        ArgumentException or UriFormatException => "The vacancy link is not a valid web address.",
        PlaywrightException => "The page could not be opened or filled (browser error or timeout).",
        _ => $"The run failed unexpectedly ({ex.GetType().Name})."
    };
}