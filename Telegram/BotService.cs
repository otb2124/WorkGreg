using WorkGreg.Services;
using Microsoft.Extensions.Options;
using global::Telegram.Bot;
using global::Telegram.Bot.Exceptions;
using global::Telegram.Bot.Polling;
using global::Telegram.Bot.Types;
using global::Telegram.Bot.Types.Enums;
using global::Telegram.Bot.Types.ReplyMarkups;

namespace WorkGreg.Telegram;

public sealed class BotService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopes,
    IOptions<BotOptions> options,
    PollRunner runner,                       // <-- NEW
    ILogger<BotService> log) : BackgroundService, IUpdateHandler
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot @{Username} is running", me.Username);

        try
        {
            // Fills the "Menu" button next to the message box in Telegram.
            await bot.SetMyCommands(new[]
            {
                new BotCommand { Command = "menu", Description = "Open the main menu" },
                new BotCommand { Command = "check", Description = "Check for new vacancies now" },
                new BotCommand { Command = "help", Description = "List of text commands" },
                new BotCommand { Command = "stop", Description = "Stop the check that is running" },
            }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not register bot commands");
        }

        bot.StartReceiving(
            this,
            new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
            ct);
    }

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } cq)
                await HandleCallbackAsync(cq, ct);
            else if (update.Message is { Text: not null } message)
                await HandleMessageAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Update {UpdateId} failed", update.Id);
        }
    }

    // ------------------------------------------------------------------ text messages

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text!;

        if (!IsAllowed(chatId))
        {
            log.LogWarning("Ignoring message from unauthorized chat {ChatId}", chatId);
            return;   // no reply sent
        }

        using var scope = scopes.CreateScope();
        var menu = scope.ServiceProvider.GetRequiredService<MenuHandler>();

        // Slash command: any pending prompt is abandoned.
        if (text.StartsWith('/'))
        {
            menu.CancelPending(chatId);

            if (CommandName(text) == "/stop")
            {
                await Reply(chatId, runner.RequestStop()
                    ? "⏹ Stopping… the check ends as soon as the request in progress is cancelled."
                    : "No check is running right now.", ct);
                return;
            }

            if (CommandName(text) == "/check")
            {
                if (runner.IsRunning)
                {
                    await Reply(chatId, "A check is already running. Send /stop to cancel it.", ct);
                    return;
                }

                await Reply(chatId, "⏳ Checking your sources… vacancies arrive as they are found. Send /stop to cancel.", ct);
                _ = RunCheckInBackgroundAsync(chatId, ct);   // not awaited on purpose, so /stop can be read meanwhile
                return;
            }

            if (CommandName(text) is "/start" or "/menu")
            {
                await SendScreen(chatId, await menu.MainAsync(chatId, null, ct), ct);
                return;
            }

            string reply;
            try
            {
                reply = await scope.ServiceProvider.GetRequiredService<CommandHandler>().HandleAsync(chatId, text, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Command failed: {Text}", text);
                reply = "Something went wrong, check the logs.";
            }

            await Reply(chatId, reply, ct);
            return;
        }

        // Plain text: either the answer to a prompt ("send me your keywords"), or noise.
        TextResult? result;
        try
        {
            result = await menu.HandleTextAsync(chatId, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Text input failed: {Text}", text);
            menu.CancelPending(chatId);
            await Reply(chatId, "Something went wrong, check the logs.", ct);
            return;
        }

        if (result is null)
        {
            await SendScreen(chatId, await menu.MainAsync(chatId, "Use the buttons below 👇", ct), ct);
            return;
        }

        await EditScreen(chatId, result.MessageId, result.Screen, ct);
        await TryDelete(chatId, message.MessageId, ct);   // keep the chat clean
    }

    // ------------------------------------------------------------------ button taps

    private async Task HandleCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (cq.Message is not { } msg || cq.Data is not { } data)
        {
            await SafeAnswer(cq.Id, null, ct);
            return;
        }

        var chatId = msg.Chat.Id;
        var messageId = msg.MessageId;

        if (!IsAllowed(chatId))
        {
            await SafeAnswer(cq.Id, null, ct);
            return;
        }

        if (data == "stop")
        {
            await SafeAnswer(cq.Id, runner.RequestStop() ? "Stopping…" : "No check is running", ct);
            return;
        }

        if (data == "chk")
        {
            if (runner.IsRunning)
            {
                await SafeAnswer(cq.Id, "A check is already running", ct);
                return;
            }

            await SafeAnswer(cq.Id, null, ct);
            await EditScreen(chatId, messageId, new Screen(
                "⏳ Checking your sources…\nVacancies arrive as they are found.",
                new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⏹ Stop", "stop"))), ct);

            _ = RunCheckFromMenuAsync(chatId, messageId, ct);
            return;
        }

        using var scope = scopes.CreateScope();
        var menu = scope.ServiceProvider.GetRequiredService<MenuHandler>();

        // Slow action (a poll cycle): stop the button spinner and show progress first.
        var slow = MenuHandler.IsSlow(data);
        if (slow)
        {
            await SafeAnswer(cq.Id, null, ct);
            await EditScreen(chatId, messageId, MenuHandler.Busy("⏳ Checking your sources…"), ct);
        }

        Screen screen;
        try
        {
            screen = await menu.HandleCallbackAsync(chatId, messageId, data, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Callback failed: {Data}", data);
            if (!slow) await SafeAnswer(cq.Id, "Something went wrong, check the logs.", ct);
            return;
        }

        if (!slow) await SafeAnswer(cq.Id, screen.Toast, ct);
        await EditScreen(chatId, messageId, screen, ct);
    }

    // ------------------------------------------------------------------ plumbing

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogError(exception, "Telegram error from {Source}", source);
        return Task.CompletedTask;
    }

    private bool IsAllowed(long chatId)
    {
        var allowed = options.Value.AllowedChatIds;
        return allowed.Length == 0 || allowed.Contains(chatId);
    }

    private static string CommandName(string text)
    {
        var cmd = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        var at = cmd.IndexOf('@');
        return at > 0 ? cmd[..at] : cmd;
    }

    private Task SendScreen(long chatId, Screen s, CancellationToken ct) =>
        bot.SendMessage(
            chatId,
            s.Html,
            parseMode: ParseMode.Html,
            replyMarkup: s.Keyboard,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            cancellationToken: ct);

    private async Task EditScreen(long chatId, int messageId, Screen s, CancellationToken ct)
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
            await SendScreen(chatId, s, ct);
        }
    }


    private async Task RunCheckInBackgroundAsync(long chatId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var reply = await scope.ServiceProvider.GetRequiredService<CommandHandler>().HandleAsync(chatId, "/check", ct);
            await Reply(chatId, reply, ct);
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Background check failed");
            try { await Reply(chatId, "Something went wrong, check the logs.", ct); } catch { /* nothing more to do */ }
        }
    }

    private async Task RunCheckFromMenuAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var menu = scope.ServiceProvider.GetRequiredService<MenuHandler>();
            var screen = await menu.HandleCallbackAsync(chatId, messageId, "chk", ct);   // runs the check, returns the main menu
            await EditScreen(chatId, messageId, screen, ct);
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Background check (menu) failed");
        }
    }

    private async Task SafeAnswer(string callbackQueryId, string? toast, CancellationToken ct)
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

    private async Task TryDelete(long chatId, int messageId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(chatId, messageId, ct); }
        catch (ApiRequestException) { /* not allowed or already gone: harmless */ }
    }

    private Task Reply(long chatId, string html, CancellationToken ct) =>
        bot.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            cancellationToken: ct);
}