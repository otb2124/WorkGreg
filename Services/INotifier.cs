using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WorkGreg.Services;

public interface INotifier
{
    /// <exception cref="RecipientUnreachableException">The chat can't receive messages (user blocked the bot, chat gone).</exception>
    /// <exception cref="RateLimitedException">Still rate limited after the retries.</exception>
    /// <exception cref="MessageRejectedException">This particular message is refused (e.g. malformed HTML); retrying won't help.</exception>
    Task SendAsync(long chatId, string html, CancellationToken ct);
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

    public async Task SendAsync(long chatId, string html, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await bot.SendMessage(
                    chatId,
                    html,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
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