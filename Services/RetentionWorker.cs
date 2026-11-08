using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

/// <summary>Runs <see cref="RetentionService"/> on a fixed interval (default: daily).</summary>
public sealed class RetentionWorker(
    RetentionService retention, IOptions<RetentionOptions> options, TimeProvider time, ILogger<RetentionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Job retention is off (Retention:JobRetentionDays is 0)");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(options.Value.StartupDelaySeconds), time, ct);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.IntervalHours), time);
        do
        {
            try
            {
                await retention.PurgeAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Retention run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}