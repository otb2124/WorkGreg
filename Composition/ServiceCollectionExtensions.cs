using System.Text.Json.Serialization;
using WorkGreg.Data;
using WorkGreg.Fetching;
using WorkGreg.Services;
using WorkGreg.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using global::Telegram.Bot;

namespace WorkGreg.Composition;

/// <summary>Program.cs reads as a list of features; each feature registers its own services here.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Binds Bot / Polling / Api and checks them at startup (the app refuses to start on a bad config).</summary>
    public static IServiceCollection AddWorkGregOptions(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<BotOptions>, BotOptionsValidator>();
        services.AddSingleton<IValidateOptions<PollOptions>, PollOptionsValidator>();
        services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();

        services.AddOptions<BotOptions>().BindConfiguration("Bot").ValidateOnStart();
        services.AddOptions<PollOptions>().BindConfiguration("Polling").ValidateOnStart();
        services.AddOptions<ApiOptions>().BindConfiguration("Api").ValidateOnStart();

        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var dbPath = configuration["Database:Path"] ?? "WorkGreg.db";
        services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));
        return services;
    }

    /// <summary>Fetch strategies: add new IJobFetcher implementations here.</summary>
    public static IServiceCollection AddFetching(this IServiceCollection services)
    {
        services.AddHttpClient("fetch", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WorkGreg/1.0 (personal job alerts)");
        });

        services.AddSingleton<HostThrottle>();
        services.AddSingleton<IJobFetcher, FeedFetcher>();
        services.AddSingleton<IJobFetcher, ApiFetcher>();
        services.AddSingleton<IJobFetcher, HtmlFetcher>();
        services.AddSingleton<IJobFetcher, BrowserFetcher>();
        services.AddSingleton<FetcherRegistry>();

        return services;
    }

    /// <summary>Use cases shared by the Telegram bot and the HTTP API. Both are thin adapters on top of these.</summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<ProfileService>();
        services.AddScoped<SourceService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<JobService>();
        return services;
    }

    public static IServiceCollection AddTelegramBot(this IServiceCollection services)
    {
        // The token is checked by BotOptionsValidator at startup, so no manual throw is needed here.
        services.AddSingleton<ITelegramBotClient>(sp =>
            new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));

        services.AddSingleton<INotifier, TelegramNotifier>();
        services.AddSingleton<PendingInputStore>();
        services.AddScoped<CommandHandler>();
        services.AddScoped<MenuHandler>();
        services.AddHostedService<BotService>();

        return services;
    }

    public static IServiceCollection AddPolling(this IServiceCollection services)
    {
        services.AddSingleton<PollCoordinator>();
        services.AddSingleton<FailureAlerter>();
        services.AddSingleton<SourceSyncService>();
        services.AddSingleton<DeliveryService>();
        services.AddSingleton<PollRunner>();
        return services;
    }

    /// <summary>JSON enum-as-string and the CORS policy for the REST API.</summary>
    public static IServiceCollection AddWorkGregApi(this IServiceCollection services, IConfiguration configuration)
    {
        // API: enums as strings ("feed" / "Feed") instead of numbers.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var api = configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
        services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (api.AllowedOrigins.Length > 0)
                p.WithOrigins(api.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }));

        return services;
    }
}