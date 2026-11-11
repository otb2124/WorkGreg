using global::Telegram.Bot;
using global::Telegram.Bot.Exceptions;
using global::Telegram.Bot.Polling;
using global::Telegram.Bot.Types;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using WorkGreg.Client;
using WorkGreg.Services;
using WorkGreg.Telegram.Menu;

namespace WorkGreg.Telegram;

/// <summary>
/// Receives every update. Its jobs: drop chats that are not allowed, drop chats that send too much, route what is left
/// to the class that handles it, and turn a failure into a log line (and, where the user is waiting, an apology).
/// What a command does lives in the command class, what a button does lives in its <see cref="ICallbackHandler"/>,
/// how a message is sent lives in <see cref="IBotIo"/>.
/// </summary>
internal sealed class UpdateDispatcher(
    IServiceScopeFactory scopes,
    IOptions<BotOptions> options,
    CommandRegistry commands,
    CallbackRouter callbacks,
    ChatRateLimiter limiter,
    PendingInputStore pending,
    CoreHealth health,
    IBotIo io,
    ILogger<UpdateDispatcher> log) : IUpdateHandler
{
    private const string Failed = "Something went wrong, check the logs.";
    private const string Unavailable = "⚠️ Service temporarily unavailable. Please try again in a minute.";

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["ChatId"] = chatId,
            ["UpdateId"] = update.Id
        });

        var started = Stopwatch.GetTimestamp();
        try
        {
            if (update.CallbackQuery is { } cq)
                await HandleCallbackAsync(cq, ct);
            else if (update.Message is { Text: not null } message)
                await HandleMessageAsync(message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reply = FailureReply(ex, $"Update {update.Id}");
            await TryNotifyAsync(update, reply, ct);
        }
        finally
        {
            // Per-tap latency, including the Telegram calls. The button's action code ("pen", "home") is the label,
            // so ids never split the groups and no user text is logged.
            var kind = update.CallbackQuery?.Data?.Split(':')[0] ?? (update.Message is not null ? "message" : "other");
            log.LogInformation("Update handled in {ElapsedMs:0} ms ({Kind})", Stopwatch.GetElapsedTime(started).TotalMilliseconds, kind);
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogError(exception, "Telegram error from {Source}", source);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ failures

    /// <summary>Logs a failure and picks the text the user sees: "temporarily unavailable" when the core is down, else the generic one.</summary>
    private string FailureReply(Exception ex, string what)
    {
        if (CoreOutage.IsOutage(ex))
        {
            health.NoteUnreachable(ex.Message);   // the monitor flips it back as soon as /health answers again
            log.LogWarning("{What}: the core is unavailable ({Reason})", what, ex.Message);
            return Unavailable;
        }

        log.LogError(ex, "{What} failed", what);
        return Failed;
    }

    /// <summary>Best effort, for a failure nobody inside the handlers caught: the user should not be left with silence.</summary>
    private async Task TryNotifyAsync(Update update, string text, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } cq)
            {
                if (cq.Message is { } m && options.Value.IsAllowed(m.Chat.Id))
                    await io.AnswerCallbackAsync(cq.Id, text, ct);
            }
            else if (update.Message is { } message && options.Value.IsAllowed(message.Chat.Id))
            {
                await io.ReplyAsync(message.Chat.Id, text, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug(ex, "Could not tell the chat about the failure");
        }
    }

    // ------------------------------------------------------------------ text messages

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text!;

        if (!options.Value.IsAllowed(chatId))
        {
            log.LogWarning("Ignoring message from unauthorized chat {ChatId}", chatId);
            return;   // no reply sent
        }

        var admission = limiter.TryAcquire(chatId);
        if (!admission.Allowed)
        {
            LogLimited(chatId, admission);
            if (admission.Notify) await io.ReplyAsync(chatId, Replies.RateLimited(admission.RetryAfter), ct);
            return;
        }

        using var scope = scopes.CreateScope();

        if (CommandLine.TryParse(text, out var line))
        {
            pending.Clear(chatId);   // a slash command abandons any pending prompt
            await RunCommandAsync(scope.ServiceProvider, chatId, line, text, ct);
            return;
        }

        await HandlePlainTextAsync(scope.ServiceProvider, message, ct);
    }

    private async Task RunCommandAsync(IServiceProvider services, long chatId, CommandLine line, string text, CancellationToken ct)
    {
        if (!commands.TryGet(line.Name, out var type))
        {
            await io.ReplyAsync(chatId, "Unknown command. Try /help", ct);
            return;
        }

        try
        {
            var command = (IBotCommand)services.GetRequiredService(type);
            await command.ExecuteAsync(new CommandContext(chatId, line.Name, line.Args, io, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await io.ReplyAsync(chatId, FailureReply(ex, $"Command {text}"), ct);
        }
    }

    /// <summary>Plain text is either the answer to a prompt ("send me your keywords"), or noise.</summary>
    private async Task HandlePlainTextAsync(IServiceProvider services, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text!;

        TextResult? result;
        try
        {
            result = await services.GetRequiredService<PendingInputHandler>().HandleAsync(chatId, text, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // When the core is down the answer was not saved, so the prompt stays and the user can simply send it again.
            if (!CoreOutage.IsOutage(ex)) pending.Clear(chatId);
            await io.ReplyAsync(chatId, FailureReply(ex, $"Text input {text}"), ct);
            return;
        }

        if (result is null)
        {
            var main = services.GetRequiredService<MainScreen>();
            await io.SendScreenAsync(chatId, await main.RenderAsync(chatId, "Use the buttons below 👇", ct), ct);
            return;
        }

        await io.EditScreenAsync(chatId, result.MessageId, result.Screen, ct);
        await io.TryDeleteAsync(chatId, message.MessageId, ct);   // keep the chat clean
    }

    // ------------------------------------------------------------------ button taps

    private async Task HandleCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (cq.Message is not { } msg || cq.Data is not { } data)
        {
            await io.AnswerCallbackAsync(cq.Id, null, ct);
            return;
        }

        var chatId = msg.Chat.Id;

        if (!options.Value.IsAllowed(chatId))
        {
            await io.AnswerCallbackAsync(cq.Id, null, ct);
            return;
        }

        // A tap is always answered (that stops the spinner), but a limited chat only gets the toast once per burst.
        var admission = limiter.TryAcquire(chatId);
        if (!admission.Allowed)
        {
            LogLimited(chatId, admission);
            await io.AnswerCallbackAsync(cq.Id, admission.Notify ? Replies.RateLimited(admission.RetryAfter) : null, ct);
            return;
        }

        // Data this bot did not produce (a very old keyboard, an edited client): show the main menu.
        if (!CallbackData.TryParse(data, out var parsed))
        {
            log.LogDebug("Unrecognized callback data {Data}", data);
            parsed = CallbackData.Home;
        }

        using var scope = scopes.CreateScope();
        try
        {
            await callbacks.RouteAsync(scope.ServiceProvider, new CallbackContext(cq.Id, chatId, msg.MessageId, parsed, io, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // No-op if the handler already answered the tap.
            await io.AnswerCallbackAsync(cq.Id, FailureReply(ex, $"Callback {data}"), ct);
        }
    }

    private void LogLimited(long chatId, RateLimitDecision decision)
    {
        // One line per burst, not one per refused update: the log must not become the next thing a flood fills.
        if (decision.Notify)
            log.LogInformation("Rate limiting chat {ChatId}, next request in {RetryAfterSeconds:0.0}s", chatId, decision.RetryAfter.TotalSeconds);
    }
}