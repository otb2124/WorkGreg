using WorkGreg.Services;
using Microsoft.Extensions.Options;
using global::Telegram.Bot;
using global::Telegram.Bot.Polling;
using global::Telegram.Bot.Types;
using global::Telegram.Bot.Types.Enums;

namespace WorkGreg.Telegram;

public sealed class BotService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopes,
    IOptions<BotOptions> options,
    ILogger<BotService> log) : BackgroundService, IUpdateHandler
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot @{Username} is running", me.Username);

        bot.StartReceiving(this, new ReceiverOptions { AllowedUpdates = [UpdateType.Message] }, ct);
    }

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message) return;

        var chatId = message.Chat.Id;
        var allowed = options.Value.AllowedChatIds;
        if (allowed.Length > 0 && !allowed.Contains(chatId))
        {
            log.LogWarning("Ignoring message from unauthorized chat {ChatId}", chatId);
            return;   // <-- no reply sent
        }

        if (!text.StartsWith('/'))
        {
            await Reply(client, chatId, "Send /help to see what I can do.", ct);
            return;
        }

        string reply;
        try
        {
            using var scope = scopes.CreateScope();
            reply = await scope.ServiceProvider.GetRequiredService<CommandHandler>().HandleAsync(chatId, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Command failed: {Text}", text);
            reply = "Something went wrong, check the logs.";
        }

        await Reply(client, chatId, reply, ct);
    }

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogError(exception, "Telegram error from {Source}", source);
        return Task.CompletedTask;
    }

    private static Task Reply(ITelegramBotClient client, long chatId, string html, CancellationToken ct) =>
        client.SendMessage(
            chatId,
            html,
            parseMode: ParseMode.Html,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
            cancellationToken: ct);
}
