using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Services;
using WorkGreg.Telegram.Menu;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram;

/// <summary>
/// Follows checks that were started from chat. The core runs the check (a check can take minutes and the user must be able
/// to send /stop meanwhile), so the command or button starts it and hands over here: this polls the core every few seconds
/// and reports the outcome, as a new message (/check) or in the tapped menu message (the button).
/// Each watcher gets its own DI scope (the update's scope is gone by then) and reports its own failures.
/// A watcher lives in memory: if the bot restarts mid-check the check still finishes and its vacancies still arrive,
/// only the closing message is lost.
/// </summary>
internal sealed class BackgroundChecks(
    IServiceScopeFactory scopes,
    IBotIo io,
    TimeProvider time,
    ILogger<BackgroundChecks> log)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Longer than any sane check; after this the watcher gives up (the check itself is not touched).</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    /// <summary>In a row. The core restarting for a few seconds is survived; a core that stays away is reported.</summary>
    private const int MaxConsecutiveErrors = 5;

    /// <summary>How a watched check ended, as a message. <see cref="Success"/> is false for "something is wrong" texts.</summary>
    private readonly record struct Report(string Html, bool Success);

    /// <summary>Watches the check just started by /check and sends the outcome as a new message when it ends.</summary>
    public void WatchFromCommand(long chatId, CancellationToken ct) => _ = WatchFromCommandAsync(chatId, ct);

    /// <summary>Watches the check just started by the "Check now" button and puts the resulting main menu into the tapped message.</summary>
    public void WatchFromMenu(long chatId, int messageId, CancellationToken ct) => _ = WatchFromMenuAsync(chatId, messageId, ct);

    private async Task WatchFromCommandAsync(long chatId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IWorkGregClient>();

            var report = await WaitForReportAsync(client, chatId, ct);
            await io.ReplyAsync(chatId, report.Html, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* app is shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Watching the check failed");
            try { await io.ReplyAsync(chatId, "Something went wrong, check the logs.", ct); } catch { /* nothing more to do */ }
        }
    }

    private async Task WatchFromMenuAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IWorkGregClient>();
            var main = scope.ServiceProvider.GetRequiredService<MainScreen>();

            // Waits for the check, then shows the main menu with the outcome above it.
            var report = await WaitForReportAsync(client, chatId, ct);
            var notice = report.Success ? Ok(report.Html) : report.Html;
            await io.EditScreenAsync(chatId, messageId, await main.RenderAsync(chatId, notice, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* app is shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Watching the check (menu) failed");
        }
    }

    private async Task<Report> WaitForReportAsync(IWorkGregClient client, long chatId, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var errors = 0;

        while (true)
        {
            await Task.Delay(PollInterval, time, ct);

            CheckStatusDto status;
            try
            {
                status = await client.GetCheckAsync(chatId, ct);
                errors = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The HttpClient already retried this GET. A few failures in a row are a restart or a blip.
                log.LogWarning(ex, "Could not read the check status ({Errors}/{Max})", errors + 1, MaxConsecutiveErrors);
                if (++errors >= MaxConsecutiveErrors)
                    return new Report(Warn("Lost contact with the job watcher while the check was running. " +
                                           "Vacancies still arrive as they are found."), false);
                continue;
            }

            // Set once the check is over; the core clears it when this chat starts the next check.
            if (status.Last is { } last) return Describe(last);

            // Not running, not waiting, and no result: the core forgot the check (it restarted while it ran).
            if (status.State == RunStatus.Idle && !status.Queued)
                return new Report(Warn("The check ended without a result, the job watcher probably restarted. Run /check again."), false);

            if (time.GetElapsedTime(started) > MaxWait)
                return new Report(Warn("The check is taking very long. I stop waiting here; vacancies still arrive as they are found."), false);
        }
    }

    private static Report Describe(CheckOutcomeDto outcome) =>
        outcome.Failed
            ? new Report(Warn("The check failed. Details are in the job watcher's log."), false)
            : new Report(CheckFinished(new PollResult(outcome.Sent, outcome.Stopped)), true);
}