using WorkGreg.Services;
using WorkGreg.Telegram.Menu;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram;

/// <summary>
/// Checks started from chat. A check can take minutes, and the user must be able to send /stop meanwhile,
/// so the update handler starts it here and returns instead of awaiting it. Each run gets its own DI scope
/// (the update's scope is gone by then) and reports its own failures.
/// </summary>
internal sealed class BackgroundChecks(IServiceScopeFactory scopes, IBotIo io, ILogger<BackgroundChecks> log)
{
    /// <summary>Starts a check for the /check command and sends the outcome as a new message when it ends.</summary>
    public void StartFromCommand(long chatId, CancellationToken ct) => _ = RunFromCommandAsync(chatId, ct);

    /// <summary>Starts a check for the "Check now" button and puts the resulting main menu into the tapped message.</summary>
    public void StartFromMenu(long chatId, int messageId, CancellationToken ct) => _ = RunFromMenuAsync(chatId, messageId, ct);

    private async Task RunFromCommandAsync(long chatId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var reply = Render(await jobs.CheckNowAsync(chatId, ct), CheckFinished);
            await io.ReplyAsync(chatId, reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* app is shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Background check failed");
            try { await io.ReplyAsync(chatId, "Something went wrong, check the logs.", ct); } catch { /* nothing more to do */ }
        }
    }

    private async Task RunFromMenuAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var main = scope.ServiceProvider.GetRequiredService<MainScreen>();

            // Runs the check, then shows the main menu with the outcome above it.
            var notice = Notice(await jobs.CheckNowAsync(chatId, ct), CheckFinished);
            await io.EditScreenAsync(chatId, messageId, await main.RenderAsync(chatId, notice, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* app is shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Background check (menu) failed");
        }
    }
}