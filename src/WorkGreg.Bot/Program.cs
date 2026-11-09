using Microsoft.Extensions.Configuration;
using WorkGreg.Client;
using WorkGreg.Composition;
using WorkGreg.Services;

namespace WorkGreg;

/// <summary>
/// Entry point of the Telegram bot as its own process. It knows nothing about the database or the fetchers:
/// everything goes through <see cref="IWorkGregClient"/>.
/// (An explicit class instead of top-level statements, so it can never clash with the Host's generated Program type.)
/// </summary>
internal static class BotProgram
{
    public static async Task Main(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // Same secrets convention as the Host: secrets.json in the content root and next to the executable,
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

        builder.Services.AddTelegramBot();

        // IWorkGregClient: the HTTP implementation is registered here in step 4 (e.g. services.AddWorkGregHttpClient(config)).

        var host = builder.Build();

        // Fail at startup with a clear message instead of at the first Telegram update.
        using (var scope = host.Services.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IWorkGregClient>() is null)
                throw new InvalidOperationException(
                    "No IWorkGregClient is registered. The standalone bot needs the HTTP client (step 4); " +
                    "until then the bot runs inside WorkGreg.Host.");
        }

        await host.RunAsync();
    }
}