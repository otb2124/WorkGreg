namespace WorkGreg.Client;

/// <summary>
/// Whether the core is reachable right now. Updated by <see cref="CoreHealthMonitor"/> (a ping every 30 s) and by the
/// dispatcher when a real request fails. Only changes are logged, so an outage is one warning and one recovery line,
/// not a line per failed request.
/// </summary>
internal sealed class CoreHealth(IHttpClientFactory factory, TimeProvider time, ILogger<CoreHealth> log)
{
    public const string ClientName = "WorkGreg.Core.Health";

    private readonly object _gate = new();
    private bool? _reachable;       // null until the first answer
    private long _downSince;

    public bool IsUnreachable
    {
        get { lock (_gate) return _reachable == false; }
    }

    /// <summary>Asks the core's /health endpoint. Never throws.</summary>
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = factory.CreateClient(ClientName);
            using var response = await http.GetAsync("health", ct);

            if (response.IsSuccessStatusCode)
            {
                Mark(true, null);
                return true;
            }

            Mark(false, $"/health answered {(int)response.StatusCode} {response.StatusCode}");
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Mark(false, ex.Message);
            return false;
        }
    }

    /// <summary>A real request just failed with an outage: do not wait for the next ping to know.</summary>
    public void NoteUnreachable(string reason) => Mark(false, reason);

    private void Mark(bool reachable, string? reason)
    {
        bool? before;
        var downFor = TimeSpan.Zero;

        lock (_gate)
        {
            before = _reachable;
            _reachable = reachable;

            if (!reachable && before != false) _downSince = time.GetTimestamp();
            if (reachable && before == false) downFor = time.GetElapsedTime(_downSince);
        }

        // From "unknown" nothing is logged: Program.cs already reports the first contact at startup.
        if (!reachable && before == true)
            log.LogWarning("The core became unreachable: {Reason}", reason);
        else if (reachable && before == false)
            log.LogInformation("The core is reachable again after {Seconds:0} s", downFor.TotalSeconds);
    }
}

/// <summary>Pings the core's /health on a timer so an outage (and its end) shows up in the log even when nobody is chatting.</summary>
internal sealed class CoreHealthMonitor(CoreHealth health, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            while (true)
            {
                await health.CheckAsync(ct);
                await timer.WaitForNextTickAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
    }
}