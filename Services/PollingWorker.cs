using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

/// <summary>Triggers a poll cycle on a fixed interval (default: every hour).</summary>
public sealed class PollingWorker(
    PollRunner runner, IOptions<PollOptions> options, TimeProvider time, ILogger<PollingWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(options.Value.StartupDelaySeconds), time, ct);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes), time);
        do
        {
            try
            {
                await runner.RunAsync(null, ct);
            }
            // Only a real shutdown (ct cancelled) may end the worker. Any other exception, including a
            // TaskCanceledException caused by an HTTP timeout, is logged and the next cycle still runs.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Poll cycle crashed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}