using global::Telegram.Bot;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WorkGreg.Services;
using WorkGreg.Telegram;
using WorkGreg.Telegram.Menu;

namespace WorkGreg.Composition;

public static class BotServiceCollectionExtensions
{
    /// <summary>
    /// Everything the Telegram front end needs. It expects IWorkGregClient (see AddWorkGregHttpClient) and
    /// IOptions&lt;BotOptions&gt; to be registered by the bot's Program.cs.
    /// </summary>
    public static IServiceCollection AddTelegramBot(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);   // rate limiter and check polling

        services.TryAddSingleton<ITelegramBotClient>(sp =>
            new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));

        services.AddSingleton<PendingInputStore>();
        services.AddScoped<PromptScreen>();
        services.AddScoped<PendingInputHandler>();

        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
        services.AddOptions<RateLimitOptions>().BindConfiguration("Bot:RateLimit").ValidateOnStart();
        services.AddSingleton<ChatRateLimiter>();

        // Telegram layer: BotService (lifecycle) -> UpdateDispatcher (auth + routing) -> command classes.
        // Every class marked [Command] / [Handles] in this assembly is found here.
        services.AddSingleton<IBotIo, BotIo>();
        services.AddSingleton<BackgroundChecks>();
        services.AddBotRoutes(typeof(BotServiceCollectionExtensions).Assembly);
        services.AddSingleton<UpdateDispatcher>();
        services.AddHostedService<BotService>();

        return services;
    }
}