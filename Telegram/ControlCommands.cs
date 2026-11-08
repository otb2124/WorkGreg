using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

/// <summary>/start and /menu: the button menu.</summary>
[Command("menu", "start", MenuText = "Open the main menu", MenuOrder = 1)]
internal sealed class MenuCommand(MenuHandler menu) : IBotCommand
{
    public async Task ExecuteAsync(CommandContext ctx) =>
        await ctx.Io.SendScreenAsync(ctx.ChatId, await menu.MainAsync(ctx.ChatId, null, ctx.Ct), ctx.Ct);
}

/// <summary>
/// Starts a check and returns at once: the check runs in <see cref="BackgroundChecks"/>, so /stop can be read meanwhile.
/// </summary>
[Command("check", MenuText = "Check for new vacancies now", MenuOrder = 2)]
internal sealed class CheckCommand(PollRunner runner, BackgroundChecks background) : IBotCommand
{
    public async Task ExecuteAsync(CommandContext ctx)
    {
        switch (runner.StatusFor(ctx.ChatId))
        {
            case RunStatus.Yours:
                await ctx.Io.ReplyAsync(ctx.ChatId, "A check is already running. Send /stop to cancel it.", ctx.Ct);
                return;
            case RunStatus.Scheduled:
                await ctx.Io.ReplyAsync(ctx.ChatId, "The hourly check is running right now. Your vacancies arrive as it goes.", ctx.Ct);
                return;
        }

        await ctx.Io.ReplyAsync(ctx.ChatId, "⏳ Checking your sources… vacancies arrive as they are found. Send /stop to cancel.", ctx.Ct);
        background.StartFromCommand(ctx.ChatId, ctx.Ct);
    }
}

[Command("stop", MenuText = "Stop the check you started", MenuOrder = 4)]
internal sealed class StopCommand(PollRunner runner) : TextCommand
{
    protected override Task<string> RunAsync(CommandContext ctx) => Task.FromResult(runner.RequestStop(ctx.ChatId) switch
    {
        StopOutcome.Stopped => "⏹ Stopping… the check ends as soon as the request in progress is cancelled.",
        StopOutcome.ScheduledRun => "The scheduled hourly check is running right now. It can't be stopped from chat.",
        _ => "No check of yours is running right now."
    });
}

[Command("resend", "resetprocessed")]
internal sealed class ResendCommand(JobService jobs) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx) =>
        Render(await jobs.ResendAllAsync(ctx.ChatId, ctx.Ct), Resent);
}

// ---------------------------------------------------------------------- buttons with their own class

/// <summary>The "⏹ Stop" button.</summary>
[Callback("stop")]
internal sealed class StopCallback(PollRunner runner) : IBotCallback
{
    public Task ExecuteAsync(CallbackContext ctx) => ctx.Io.AnswerCallbackAsync(ctx.QueryId, runner.RequestStop(ctx.ChatId) switch
    {
        StopOutcome.Stopped => "Stopping…",
        StopOutcome.ScheduledRun => "The hourly check can't be stopped from chat",
        _ => "No check of yours is running"
    }, ctx.Ct);
}

/// <summary>The "🔎 Check now" button: shows progress with a Stop button, then runs the check in the background.</summary>
[Callback("chk")]
internal sealed class CheckCallback(PollRunner runner, BackgroundChecks background) : IBotCallback
{
    public async Task ExecuteAsync(CallbackContext ctx)
    {
        switch (runner.StatusFor(ctx.ChatId))
        {
            case RunStatus.Yours:
                await ctx.Io.AnswerCallbackAsync(ctx.QueryId, "A check is already running", ctx.Ct);
                return;
            case RunStatus.Scheduled:
                await ctx.Io.AnswerCallbackAsync(ctx.QueryId, "The hourly check is running right now", ctx.Ct);
                return;
        }

        await ctx.Io.AnswerCallbackAsync(ctx.QueryId, null, ctx.Ct);
        await ctx.Io.EditScreenAsync(ctx.ChatId, ctx.MessageId, new Screen(
            "⏳ Checking your sources…\nVacancies arrive as they are found.",
            new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⏹ Stop", "stop"))), ctx.Ct);

        background.StartFromMenu(ctx.ChatId, ctx.MessageId, ctx.Ct);
    }
}