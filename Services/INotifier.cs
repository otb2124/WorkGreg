using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WorkGreg.Services;

public interface INotifier
{
    Task SendAsync(long chatId, string html, CancellationToken ct);
}

public sealed class TelegramNotifier(ITelegramBotClient bot) : INotifier
{
    public Task SendAsync(long chatId, string html, CancellationToken ct) =>
        bot.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            cancellationToken: ct);
}
