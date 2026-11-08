using Microsoft.Extensions.Options;
using WorkGreg.Services;
using global::Telegram.Bot;
using global::Telegram.Bot.Exceptions;
using global::Telegram.Bot.Polling;
using global::Telegram.Bot.Types;

namespace WorkGreg.Telegram;

/// <summary>
/// Receives every update. Its three jobs: drop chats that are not allowed, route what is left to the class that
/// handles it, and turn a failure into a log line (and, where the user is waiting, an apology).
/// What a command does lives in the command class; how a message is sent lives in <see cref="IBotIo"/>.
/// </summary>
internal sealed class UpdateDispatcher(
    IServiceScopeFactory scopes,
    IOptions<BotOptions> options,
    CommandRegistry commands,
    CallbackRegistry callbacks,
    IBotIo io,
    ILogger<UpdateDispatcher> log) : IUpdateHandler
{
    private const string Failed = "Something went wrong, check the logs.";

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } cq)
                await HandleCallbackAsync(cq, ct);
            else if (update.Message is { Text: not null } message)
                await HandleMessageAsync(message, ct);
        }
        // Only a real shutdown is allowed to escape. A Telegram/HTTP timeout is also a cancellation
        // exception, and it must not be rethrown into the receiver loop.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Update {UpdateId} failed", update.Id);
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogError(exception, "Telegram error from {Source}", source);
        return Task.CompletedTask;
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

        using var scope = scopes.CreateScope();
        var menu = scope.ServiceProvider.GetRequiredService<MenuHandler>();

        if (CommandLine.TryParse(text, out var line))
        {
            menu.CancelPending(chatId);   // a slash command abandons any pending prompt
            await RunCommandAsync(scope.ServiceProvider, chatId, line, text, ct);
            return;
        }

        await HandlePlainTextAsync(menu, message, ct);
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
            log.LogError(ex, "Command failed: {Text}", text);
            await io.ReplyAsync(chatId, Failed, ct);
        }
    }

    /// <summary>Plain text is either the answer to a prompt ("send me your keywords"), or noise.</summary>
    private async Task HandlePlainTextAsync(MenuHandler menu, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text!;

        TextResult? result;
        try
        {
            result = await menu.HandleTextAsync(chatId, text, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Text input failed: {Text}", text);
            menu.CancelPending(chatId);
            await io.ReplyAsync(chatId, Failed, ct);
            return;
        }

        if (result is null)
        {
            await io.SendScreenAsync(chatId, await menu.MainAsync(chatId, "Use the buttons below 👇", ct), ct);
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
        var messageId = msg.MessageId;

        if (!options.Value.IsAllowed(chatId))
        {
            await io.AnswerCallbackAsync(cq.Id, null, ct);
            return;
        }

        using var scope = scopes.CreateScope();

        // Buttons with their own class.
        if (callbacks.TryGet(data, out var type))
        {
            var callback = (IBotCallback)scope.ServiceProvider.GetRequiredService(type);
            await callback.ExecuteAsync(new CallbackContext(cq.Id, chatId, messageId, data, io, ct));
            return;
        }

        // Everything else is a menu page.
        var menu = scope.ServiceProvider.GetRequiredService<MenuHandler>();

        // Slow action (a poll cycle): stop the button spinner and show progress first.
        var slow = MenuHandler.IsSlow(data);
        if (slow)
        {
            await io.AnswerCallbackAsync(cq.Id, null, ct);
            await io.EditScreenAsync(chatId, messageId, MenuHandler.Busy("⏳ Checking your sources…"), ct);
        }

        Screen screen;
        try
        {
            screen = await menu.HandleCallbackAsync(chatId, messageId, data, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Callback failed: {Data}", data);
            if (!slow) await io.AnswerCallbackAsync(cq.Id, Failed, ct);
            return;
        }

        if (!slow) await io.AnswerCallbackAsync(cq.Id, screen.Toast, ct);
        await io.EditScreenAsync(chatId, messageId, screen, ct);
    }
}