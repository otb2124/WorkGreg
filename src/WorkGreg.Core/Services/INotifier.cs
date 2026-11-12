using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace WorkGreg.Services;

/// <summary>
/// An inline button under a message. <paramref name="CallbackData"/> is what Telegram hands back to the bot when it is tapped
/// (max 64 bytes). <paramref name="NewRow"/> false puts the button on the row of the previous one.
/// </summary>
public readonly record struct NotifyButton(string Text, string CallbackData, bool NewRow = true);

public interface INotifier
{
    /// <exception cref="RecipientUnreachableException">The chat can't receive messages (user blocked the bot, chat gone).</exception>
    /// <exception cref="RateLimitedException">Still rate limited after the retries.</exception>
    /// <exception cref="MessageRejectedException">This particular message is refused (e.g. malformed HTML); retrying won't help.</exception>
    /// <param name="buttons">Inline buttons under the message; null or empty = none (what every existing caller does).</param>
    Task SendAsync(long chatId, string html, CancellationToken ct, IReadOnlyList<NotifyButton>? buttons = null);
}

/// <summary>The chat can't be reached at all (HTTP 403, or "chat not found"). Everything for this chat should stop.</summary>
public sealed class RecipientUnreachableException(long chatId, string reason)
    : Exception($"Chat {chatId} can't be reached: {reason}")
{
    public long ChatId { get; } = chatId;
}

/// <summary>Telegram kept answering 429 (Too Many Requests). Try this chat again next cycle.</summary>
public sealed class RateLimitedException(long chatId)
    : Exception($"Telegram is rate limiting chat {chatId}.")
{
    public long ChatId { get; } = chatId;
}

/// <summary>Telegram refused this one message (HTTP 400). The chat itself is fine.</summary>
public sealed class MessageRejectedException(long chatId, string reason)
    : Exception($"Telegram rejected a message for chat {chatId}: {reason}")
{
    public long ChatId { get; } = chatId;
}

public sealed class TelegramNotifier(ITelegramBotClient bot) : INotifier
{
    private const int MaxRateLimitRetries = 2;
    private const int MaxRetryAfterSeconds = 30;

    public async Task SendAsync(long chatId, string html, CancellationToken ct, IReadOnlyList<NotifyButton>? buttons = null)
    {
        var keyboard = ToKeyboard(buttons);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await bot.SendMessage(
                    chatId,
                    html,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: keyboard,
                    cancellationToken: ct);
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt < MaxRateLimitRetries)
            {
                // Telegram says how long to wait; respect it (capped, so one chat can't stall the whole cycle).
                var seconds = Math.Clamp(ex.Parameters?.RetryAfter ?? 5, 1, MaxRetryAfterSeconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
            catch (ApiRequestException ex)
            {
                throw Translate(chatId, ex);
            }
        }
    }

    /// <summary>Buttons to Telegram's keyboard: a button starts a new row unless it says otherwise. Null when there is nothing to show.</summary>
    private static InlineKeyboardMarkup? ToKeyboard(IReadOnlyList<NotifyButton>? buttons)
    {
        if (buttons is null || buttons.Count == 0) return null;

        var rows = new List<List<InlineKeyboardButton>>();
        foreach (var button in buttons)
        {
            var telegramButton = InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData);
            if (rows.Count == 0 || button.NewRow) rows.Add([telegramButton]);
            else rows[^1].Add(telegramButton);
        }

        return new InlineKeyboardMarkup(rows);
    }

    private static Exception Translate(long chatId, ApiRequestException ex) => ex.ErrorCode switch
    {
        403 => new RecipientUnreachableException(chatId, ex.Message),
        429 => new RateLimitedException(chatId),
        400 when ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
            => new RecipientUnreachableException(chatId, ex.Message),
        400 => new MessageRejectedException(chatId, ex.Message),
        _ => ex
    };
}
