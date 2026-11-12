using global::Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using WorkGreg.Client;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Services;

namespace WorkGreg.Composition;

/// <summary>The core's features. Program.cs reads as a list of them; each registers its own services here.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The one clock. Everything that needs "now", a delay or a timer takes a TimeProvider, so tests can swap in a
    /// FakeTimeProvider (Microsoft.Extensions.TimeProvider.Testing) and move time by hand.
    /// </summary>
    public static IServiceCollection AddClock(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Binds Bot / Polling / Retention / Api and checks them at startup (the app refuses to start on a bad config).</summary>
    public static IServiceCollection AddWorkGregOptions(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<BotOptions>, BotOptionsValidator>();
        services.AddSingleton<IValidateOptions<PollOptions>, PollOptionsValidator>();
        services.AddSingleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>();
        services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
        services.AddSingleton<IValidateOptions<ApplierOptions>, ApplierOptionsValidator>();

        services.AddOptions<BotOptions>().BindConfiguration("Bot").ValidateOnStart();
        services.AddOptions<PollOptions>().BindConfiguration("Polling").ValidateOnStart();
        services.AddOptions<RetentionOptions>().BindConfiguration("Retention").ValidateOnStart();
        services.AddOptions<ApiOptions>().BindConfiguration("Api").ValidateOnStart();
        services.AddOptions<ApplierOptions>().BindConfiguration("Applier").ValidateOnStart();

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
            // No fixed HttpClient.Timeout: it would cut retries short. The resilience handler below owns all timeouts.
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WorkGreg/1.0 (personal job alerts)");
        })
            // Retries transient failures (HTTP 5xx, 408, 429, network errors, attempt timeouts) with exponential backoff
            // and jitter, so a one-off 503 never reaches SourceSyncService and never counts toward the 3-failure alert.
            // Only a request that still fails after every retry counts as one failure.
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);          // one try
                o.Retry.MaxRetryAttempts = 3;                                 // 1 try + 3 retries
                o.Retry.Delay = TimeSpan.FromSeconds(2);                      // 2s, 4s, 8s (+ jitter)
                o.Retry.BackoffType = DelayBackoffType.Exponential;
                o.Retry.UseJitter = true;
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(100);    // must exceed 4 x 15s + the backoff
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // must be >= 2 x AttemptTimeout
            });

        services.AddSingleton<HostThrottle>();
        services.AddSingleton<FeedCache>();
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
        services.AddScoped<AutofillService>();
        services.AddScoped<ApplicationService>();

        // The bot's only door into the services above (see IWorkGregClient).
        services.AddScoped<IWorkGregClient, InProcessWorkGregClient>();
        return services;
    }
    /// <summary>
    /// Outgoing vacancy messages. The core only calls sendMessage, never getUpdates, so it may share the bot's token with
    /// the bot process. The client is registered with TryAdd: whoever registers it first wins (same token either way).
    /// </summary>
    public static IServiceCollection AddTelegramNotifier(this IServiceCollection services)
    {
        services.TryAddSingleton<ITelegramBotClient>(sp =>
            new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));

        services.AddSingleton<INotifier, TelegramNotifier>();
        return services;
    }

    public static IServiceCollection AddPolling(this IServiceCollection services)
    {
        services.AddSingleton<PollCoordinator>();
        services.AddSingleton<FailureAlerter>();
        services.AddSingleton<SourceSyncService>();
        services.AddSingleton<DeliveryService>();
        services.AddSingleton<PollRunner>();
        services.AddSingleton<ManualCheckTracker>();   // starts a user's check in the background for the HTTP API
        services.AddHostedService<PollingWorker>();

        services.AddSingleton<RetentionService>();
        services.AddHostedService<RetentionWorker>();
        return services;
    }

    /// <summary>
    /// Loads providers.json and checks it (unique ids, every Api FieldMap has title + url, valid URL templates...).
    /// A bad file stops the app at startup with one message listing every problem, like the options validators do.
    /// Optional config key <c>Catalog:Path</c> points to another file (relative paths are resolved from the app folder).
    /// </summary>
    public static IServiceCollection AddSourceCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        var configured = configuration["Catalog:Path"];
        var path = string.IsNullOrWhiteSpace(configured) ? SourceCatalog.DefaultPath
            : Path.IsPathRooted(configured) ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

        SourceCatalog.Load(path);
        return services;
    }

}
