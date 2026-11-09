using WorkGreg.Client;
using WorkGreg.Services;
using WorkGreg.Telegram.Menu;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

/// <summary>/start and /menu: the button menu.</summary>
[Command("menu", "start", MenuText = "Open the main menu", MenuOrder = 1)]
internal sealed class MenuCommand(MainScreen main) : IBotCommand
{
    public async Task ExecuteAsync(CommandContext ctx) =>
        await ctx.Io.SendScreenAsync(ctx.ChatId, await main.RenderAsync(ctx.ChatId, null, ctx.Ct), ctx.Ct);
}

/// <summary>
/// Starts a check and returns at once: the core runs it, <see cref="BackgroundChecks"/> watches it, so /stop can be read meanwhile.
/// </summary>
[Command("check", MenuText = "Check for new vacancies now", MenuOrder = 2)]
internal sealed class CheckCommand(IWorkGregClient client, BackgroundChecks background) : IBotCommand
{
    public async Task ExecuteAsync(CommandContext ctx)
    {
        switch ((await client.GetCheckAsync(ctx.ChatId, ctx.Ct)).State)
        {
            case RunStatus.Yours:
                await ctx.Io.ReplyAsync(ctx.ChatId, "A check is already running. Send /stop to cancel it.", ctx.Ct);
                return;
            case RunStatus.Scheduled:
                await ctx.Io.ReplyAsync(ctx.ChatId, "The hourly check is running right now. Your vacancies arrive as it goes.", ctx.Ct);
                return;
        }

        // Start is the authoritative answer (the status above can be stale by now): a refusal is shown as the error.
        var start = await client.StartCheckAsync(ctx.ChatId, ctx.Ct);
        await ctx.Io.ReplyAsync(ctx.ChatId,
            Render(start, _ => "⏳ Checking your sources… vacancies arrive as they are found. Send /stop to cancel."), ctx.Ct);

        if (start.IsSuccess) background.WatchFromCommand(ctx.ChatId, ctx.Ct);
    }
}

[Command("stop", MenuText = "Stop the check you started", MenuOrder = 4)]
internal sealed class StopCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx) =>
        await client.RequestStopAsync(ctx.ChatId, ctx.Ct) switch
        {
            StopOutcome.Stopped => "⏹ Stopping… the check ends as soon as the request in progress is cancelled.",
            StopOutcome.ScheduledRun => "The scheduled hourly check is running right now. It can't be stopped from chat.",
            _ => "No check of yours is running right now."
        };
}

[Command("resend", "resetprocessed")]
internal sealed class ResendCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx) =>
        Render(await client.ResendAllAsync(ctx.ChatId, ctx.Ct), Resent);
}