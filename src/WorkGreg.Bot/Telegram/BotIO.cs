using global::Telegram.Bot;
using global::Telegram.Bot.Exceptions;
using global::Telegram.Bot.Types;
using global::Telegram.Bot.Types.Enums;

namespace WorkGreg.Telegram;

/// <summary>Everything the bot says to Telegram. Commands and the dispatcher talk to this, never to the client directly.</summary>
internal interface IBotIo
{
    /// <summary>Sends a new HTML message.</summary>
    Task ReplyAsync(long chatId, string html, CancellationToken ct);

    /// <summary>Sends a menu page as a new message.</summary>
    Task SendScreenAsync(long chatId, Screen screen, CancellationToken ct);

    /// <summary>Replaces an existing menu message. Sends a fresh one if the old one is gone.</summary>
    Task EditScreenAsync(long chatId, int messageId, Screen screen, CancellationToken ct);

    /// <summary>Stops the spinner on a tapped button, optionally with a toast. Never throws for an expired query.</summary>
    Task AnswerCallbackAsync(string callbackQueryId, string? toast, CancellationToken ct);

    /// <summary>Deletes a message; "not allowed" and "already gone" are fine.</summary>
    Task TryDeleteAsync(long chatId, int messageId, CancellationToken ct);
}

internal sealed class BotIo(ITelegramBotClient bot, ILogger<BotIo> log) : IBotIo
{
    public Task ReplyAsync(long chatId, string html, CancellationToken ct) =>
        bot.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            cancellationToken: ct);

    public Task SendScreenAsync(long chatId, Screen s, CancellationToken ct) =>
        bot.SendMessage(
            chatId,
            s.Html,
            parseMode: ParseMode.Html,
            replyMarkup: s.Keyboard,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            cancellationToken: ct);

    public async Task EditScreenAsync(long chatId, int messageId, Screen s, CancellationToken ct)
    {
        try
        {
            await bot.EditMessageText(
                chatId,
                messageId,
                s.Html,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyMarkup: s.Keyboard,
                cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Same content as before (e.g. tapping the same button twice): nothing to do.
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("can't be edited", StringComparison.OrdinalIgnoreCase)
                                          || ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Menu message is gone ({Reason}), sending a fresh one", ex.Message);
            await SendScreenAsync(chatId, s, ct);
        }
    }

    public async Task AnswerCallbackAsync(string callbackQueryId, string? toast, CancellationToken ct)
    {
        try
        {
            await bot.AnswerCallbackQuery(callbackQueryId, text: toast, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            log.LogDebug(ex, "AnswerCallbackQuery failed (usually an expired query)");
        }
    }

    public async Task TryDeleteAsync(long chatId, int messageId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(chatId, messageId, ct); }
        catch (ApiRequestException) { /* not allowed or already gone: harmless */ }
    }
}