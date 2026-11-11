using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using WorkGreg.Client;

namespace WorkGreg.Composition;

public static class CoreClientServiceCollectionExtensions
{
    private const string ReadClient = "WorkGreg.Core.Read";
    private const string WriteClient = "WorkGreg.Core.Write";

    /// <summary>
    /// <see cref="IWorkGregClient"/> over HTTP: base URL and key from <c>Core:BaseUrl</c> / <c>Core:ApiKey</c> (checked at
    /// startup), timeouts from the resilience handler. GET requests are retried on transient failures; POST/PATCH/DELETE
    /// are sent once, because a request whose answer was lost may well have been done already (a second "create profile"
    /// would make a duplicate). A third client, without retries, pings the core's /health for <see cref="CoreHealth"/>.
    /// </summary>
    public static IServiceCollection AddWorkGregHttpClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IValidateOptions<CoreOptions>, CoreOptionsValidator>();
        services.AddOptions<CoreOptions>().BindConfiguration("Core").ValidateOnStart();
        services.AddSingleton<MetaCache>();

        void Configure(IServiceProvider sp, HttpClient c)
        {
            var core = sp.GetRequiredService<IOptions<CoreOptions>>().Value;

            // The trailing slash matters: relative paths ("api/meta") are appended to it.
            c.BaseAddress = new Uri(core.BaseUrl.TrimEnd('/') + "/");
            c.DefaultRequestHeaders.Add("X-Api-Key", core.ApiKey);

            // No fixed HttpClient.Timeout: it would cut retries short. The resilience handler owns all timeouts.
            c.Timeout = Timeout.InfiniteTimeSpan;
        }

        // Reads: transient failures (5xx, 408, 429, network errors, attempt timeouts) are retried with exponential
        // backoff and jitter, so a core that restarts for a few seconds does not fail the chat.
        services.AddHttpClient(ReadClient, Configure)
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);          // one try
                o.Retry.MaxRetryAttempts = 2;                                 // 1 try + 2 retries
                o.Retry.Delay = TimeSpan.FromMilliseconds(500);               // 0.5s, 1s (+ jitter)
                o.Retry.BackoffType = DelayBackoffType.Exponential;
                o.Retry.UseJitter = true;
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(40);     // must exceed 3 x 10s + the backoff
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // must be >= 2 x AttemptTimeout
            });

        // Writes: same timeouts and circuit breaker, but nothing is ever retried (MaxRetryAttempts cannot be 0, so the
        // predicate says "never retry" instead).
        services.AddHttpClient(WriteClient, Configure)
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                o.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);     // one try + a little slack
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // must be >= 2 x AttemptTimeout
            });

        // Health pings: one quick try, no retries and no circuit breaker. A ping has to say what is true right now,
        // and must not be refused because the breaker of the read/write clients is open.
        services.AddHttpClient(CoreHealth.ClientName, Configure)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<CoreHealth>();
        services.AddHostedService<CoreHealthMonitor>();

        services.AddTransient<IWorkGregClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new HttpWorkGregClient(
                factory.CreateClient(ReadClient),
                factory.CreateClient(WriteClient),
                sp.GetRequiredService<MetaCache>());
        });

        return services;
    }
}