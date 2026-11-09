using Microsoft.Extensions.Configuration;
using WorkGreg.Client;
using WorkGreg.Composition;
using WorkGreg.Services;

namespace WorkGreg;

/// <summary>
/// Entry point of the Telegram bot as its own process. It knows nothing about the database or the fetchers: everything
/// goes through <see cref="IWorkGregClient"/>, which here is an HTTP client of the core's REST API.
/// (An explicit class instead of top-level statements, so it can never clash with another project's generated Program type.)
/// </summary>
internal static class BotProgram
{
    public static async Task Main(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // Secrets (Bot:Token, Core:ApiKey, ...) live in secrets.json in the content root and next to the executable,
        // never committed; environment variables are added again afterwards so they still win.
        builder.Configuration
            .AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "secrets.json"), optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "secrets.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        // The bot cannot reach BotOptionsValidator (it lives in the Core), so it checks the same two rules itself.
        builder.Services.AddOptions<BotOptions>()
            .BindConfiguration("Bot")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Token),
                "Bot:Token is not set. Use secrets.json or the Bot__Token env var.")
            .Validate(o => o.Access == AccessMode.Open || o.AllowedChatIds.Length > 0,
                "Bot:Access is Allowlist but Bot:AllowedChatIds is empty, so nobody could use the bot. " +
                "Add chat ids, or set Bot:Access to Open.")
            .ValidateOnStart();

        builder.Services.AddWorkGregHttpClient();   // IWorkGregClient -> the core's REST API (Core:BaseUrl, Core:ApiKey)
        builder.Services.AddTelegramBot();

        var host = builder.Build();

        // Not fatal (the core may start a moment after the bot), but a wrong URL or key shows up here, not at the first message.
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorkGreg.Bot");
        try
        {
            using var scope = host.Services.CreateScope();
            var meta = await scope.ServiceProvider.GetRequiredService<IWorkGregClient>().GetMetaAsync(CancellationToken.None);
            log.LogInformation("The core is reachable: {Providers} sources, {Regions} regions", meta.Providers.Count, meta.Regions.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The core is not reachable (or rejected the key) yet. The bot keeps running; check Core:BaseUrl and Core:ApiKey.");
        }

        await host.RunAsync();
    }
}