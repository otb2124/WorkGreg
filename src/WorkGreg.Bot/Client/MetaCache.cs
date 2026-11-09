namespace WorkGreg.Client;

/// <summary>
/// Remembers the core's catalog and limits for a few minutes. Nearly every screen asks for them, and they only change
/// when the core is redeployed. If the core cannot be reached while the copy is stale, the stale copy is used for another
/// half minute instead of failing the screen.
/// </summary>
internal sealed class MetaCache(TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkGregMeta? _value;
    private DateTimeOffset _expiresUtc;

    public async Task<WorkGregMeta> GetAsync(Func<CancellationToken, Task<WorkGregMeta>> load, CancellationToken ct)
    {
        if (TryFresh(out var fresh)) return fresh;

        await _gate.WaitAsync(ct);
        try
        {
            // Somebody else may have loaded it while this call waited.
            if (TryFresh(out fresh)) return fresh;

            try
            {
                _value = await load(ct);
                _expiresUtc = time.GetUtcNow() + Ttl;
                return _value;
            }
            catch (Exception) when (_value is not null && !ct.IsCancellationRequested)
            {
                _expiresUtc = time.GetUtcNow() + RetryAfterFailure;
                return _value!;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryFresh(out WorkGregMeta meta)
    {
        var value = _value;
        meta = value!;
        return value is not null && time.GetUtcNow() < _expiresUtc;
    }
}
