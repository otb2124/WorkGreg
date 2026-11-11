namespace WorkGreg.Client;

/// <summary>
/// Remembers the core's catalog and limits for a few minutes. Nearly every screen asks for them, and they only change
/// when the core is redeployed. If the core cannot be reached while the copy is stale, the stale copy is used for another
/// half minute instead of failing the screen.
/// Logs one line per reload with the number of answers served from memory since the previous one, which is how to
/// see that the cache is doing its job.
/// </summary>
internal sealed class MetaCache(TimeProvider time, ILogger<MetaCache>? log = null)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkGregMeta? _value;
    private DateTimeOffset _expiresUtc;
    private long _hitsSinceLoad;

    public async Task<WorkGregMeta> GetAsync(Func<CancellationToken, Task<WorkGregMeta>> load, CancellationToken ct)
    {
        if (TryFresh(out var fresh)) return Hit(fresh);

        await _gate.WaitAsync(ct);
        try
        {
            // Somebody else may have loaded it while this call waited.
            if (TryFresh(out fresh)) return Hit(fresh);

            try
            {
                _value = await load(ct);
                _expiresUtc = time.GetUtcNow() + Ttl;

                log?.LogInformation("Loaded the core's catalog ({Hits} cached answers served since the last load)",
                    Interlocked.Exchange(ref _hitsSinceLoad, 0));
                return _value;
            }
            catch (Exception ex) when (_value is not null && !ct.IsCancellationRequested)
            {
                _expiresUtc = time.GetUtcNow() + RetryAfterFailure;
                log?.LogWarning("Could not reload the core's catalog ({Reason}); using the stale copy for {Seconds:0} s",
                    ex.Message, RetryAfterFailure.TotalSeconds);
                return _value!;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkGregMeta Hit(WorkGregMeta meta)
    {
        Interlocked.Increment(ref _hitsSinceLoad);
        return meta;
    }

    private bool TryFresh(out WorkGregMeta meta)
    {
        var value = _value;
        meta = value!;
        return value is not null && time.GetUtcNow() < _expiresUtc;
    }
}