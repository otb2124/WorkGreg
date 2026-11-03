using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

/// <summary>Triggers a poll cycle on a fixed interval (default: every hour).</summary>
public sealed class PollingWorker(PollRunner runner, IOptions<PollOptions> options, ILogger<PollingWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(options.Value.StartupDelaySeconds), ct);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
        do
        {
            try
            {
                await runner.RunAsync(null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Poll cycle crashed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
