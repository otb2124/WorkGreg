using System.Globalization;
using WorkGreg.Telegram.Menu;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

/// <summary>
/// <c>/af_123</c>, the tappable link at the end of a digest line (see <see cref="CommandLine"/>, which turns it into <c>/af 123</c>).
/// A digest is one message and cannot carry a button per vacancy, so this is its 🤖 AutoFill button. Same answer as the button.
/// </summary>
[Command("af")]
internal sealed class AutofillCommand(AutofillStartHandler start) : IBotCommand
{
    public async Task ExecuteAsync(CommandContext ctx)
    {
        if (!int.TryParse(ctx.Args.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var deliveryId) || deliveryId <= 0)
        {
            await ctx.Io.ReplyAsync(ctx.ChatId, Usage("tap the 🤖 /af_… link of a vacancy in a digest"), ctx.Ct);
            return;
        }

        await ctx.Io.SendScreenAsync(ctx.ChatId, await start.RenderAsync(ctx.ChatId, deliveryId, ctx.Ct), ctx.Ct);
    }
}
