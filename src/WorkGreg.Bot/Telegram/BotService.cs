using Microsoft.Extensions.Options;
using WorkGreg.Services;
using global::Telegram.Bot;
using global::Telegram.Bot.Polling;
using global::Telegram.Bot.Types;
using global::Telegram.Bot.Types.Enums;

namespace WorkGreg.Telegram;

/// <summary>
/// Lifecycle only: connects to Telegram, publishes the command list, and starts feeding updates to the
/// <see cref="UpdateDispatcher"/>. What happens to an update is not decided here.
/// </summary>
internal sealed class BotService(
    ITelegramBotClient bot,
    UpdateDispatcher dispatcher,
    CommandRegistry commands,
    IOptions<BotOptions> options,
    ILogger<BotService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot @{Username} is running (access: {Access})", me.Username, options.Value.Access);

        try
        {
            // Fills the "Menu" button next to the message box in Telegram, from the [Command(MenuText = ...)] attributes.
            await bot.SetMyCommands(
                commands.MenuEntries.Select(e => new BotCommand { Command = e.Name, Description = e.Text }),
                cancellationToken: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not register bot commands");
        }

        bot.StartReceiving(
            dispatcher,
            new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
            ct);
    }
}