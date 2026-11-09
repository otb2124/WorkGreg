using WorkGreg.Client;
using WorkGreg.Services;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>The "🔁 Yes, reset" button.</summary>
[Handles(CallbackAction.ResendConfirm)]
internal sealed class ResendHandler(IWorkGregClient client, MainScreen mainScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx) =>
        CallbackResult.Show(await mainScreen.RenderAsync(ctx.ChatId, Notice(await client.ResendAllAsync(ctx.ChatId, ctx.Ct), Resent), ctx.Ct));
}

/// <summary>The "⏹ Stop" button.</summary>
[Handles(CallbackAction.Stop)]
internal sealed class StopHandler(IWorkGregClient client) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx) =>
        CallbackResult.Answer(await client.RequestStopAsync(ctx.ChatId, ctx.Ct) switch
        {
            StopOutcome.Stopped => "Stopping…",
            StopOutcome.ScheduledRun => "The hourly check can't be stopped from chat",
            _ => "No check of yours is running"
        });
}

/// <summary>
/// The "🔎 Check now" button: starts the check on the core, shows progress with a Stop button, then watches it in the background.
/// It talks to Telegram itself (and returns <see cref="CallbackResult.Handled"/>) because the progress message must be
/// in place before the watching starts: a quick check would otherwise finish first and be overwritten by "Checking…".
/// </summary>
[Handles(CallbackAction.Check)]
internal sealed class CheckHandler(IWorkGregClient client, BackgroundChecks background) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        switch ((await client.GetCheckAsync(ctx.ChatId, ctx.Ct)).State)
        {
            case RunStatus.Yours:
                return CallbackResult.Answer("A check is already running");
            case RunStatus.Scheduled:
                return CallbackResult.Answer("The hourly check is running right now");
        }

        // Start is the authoritative answer (the status above can be stale by now).
        var start = await client.StartCheckAsync(ctx.ChatId, ctx.Ct);
        if (start.IsFailure) return CallbackResult.Answer(start.Error.Message);

        await ctx.Io.AnswerCallbackAsync(ctx.QueryId, null, ctx.Ct);
        await ctx.Io.EditScreenAsync(ctx.ChatId, ctx.MessageId, new Screen(
            "⏳ Checking your sources…\nVacancies arrive as they are found.",
            Kb(Row(Btn("⏹ Stop", CallbackData.Stop)))), ctx.Ct);

        // Watching starts only after the progress message is in place; the first poll is a few seconds away,
        // so even a very quick check cannot finish before it.
        background.WatchFromMenu(ctx.ChatId, ctx.MessageId, ctx.Ct);
        return CallbackResult.Handled;
    }
}