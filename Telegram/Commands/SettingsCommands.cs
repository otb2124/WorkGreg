using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

[Command("pause")]
internal sealed class PauseCommand(SettingsService settings) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx) =>
        Render(await settings.SetPausedAsync(ctx.ChatId, true, ctx.Ct), Paused);
}

[Command("resume")]
internal sealed class ResumeCommand(SettingsService settings) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx) =>
        Render(await settings.SetPausedAsync(ctx.ChatId, false, ctx.Ct), Paused);
}

[Command("delivery")]
internal sealed class DeliveryCommand(SettingsService settings) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        const string usage = "/delivery instant  |  /delivery digest &lt;0-23 UTC&gt;";
        var parts = ctx.Args.Trim().ToLowerInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return Usage(usage);

        switch (parts[0])
        {
            case "instant":
                return Render(await settings.SetDeliveryAsync(ctx.ChatId, DeliveryMode.Instant, null, ctx.Ct), Replies.Delivery);

            case "digest":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var hour))
                    return Usage("/delivery digest &lt;0-23 UTC&gt;  (e.g. /delivery digest 9)");
                return Render(await settings.SetDeliveryAsync(ctx.ChatId, DeliveryMode.Digest, hour, ctx.Ct), Replies.Delivery);

            default:
                return Usage(usage);
        }
    }
}