using WorkGreg.Domain;

namespace WorkGreg.Fetching;

/// <summary>
/// Fetches for ONE poll cycle. The cycle loop still handles sources one at a time (database work and delivery are
/// sequential), but the network requests for the sources that come next are started ahead of time, so slow hosts
/// overlap.
///
/// - At most <c>degree</c> requests are in flight at once, and at most <c>degree</c> results wait unconsumed.
/// - <see cref="HostThrottle"/> still spaces out requests to the same host, so the lookahead prefers sources on
///   hosts that nobody is waiting on; a second request to a busy host would only queue behind the first.
/// - Searches with the same URL cost one request, even when they are in flight at the same time. A failed fetch is
///   shared too (the second search sees the same failure without hitting the site again).
/// - Only the cycle loop calls <see cref="Prefetch"/> and <see cref="GetAsync"/>; the fetches themselves run in parallel
///   but never touch the database.
/// </summary>
public sealed class FetchPipeline : IAsyncDisposable
{
    private readonly FetcherRegistry _fetchers;
    private readonly int _degree;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, Task<IReadOnlyList<FetchedJob>>> _started = new();
    private readonly Dictionary<string, string> _unconsumedHosts = new();   // cache key -> host

    public FetchPipeline(FetcherRegistry fetchers, int degree, CancellationToken ct)
    {
        _fetchers = fetchers;
        _degree = Math.Max(1, degree);
        _slots = new SemaphoreSlim(_degree, _degree);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>Same key = same request: searches that share a URL are fetched once.</summary>
    public static string KeyOf(FetchContext ctx) =>
        // The keyword only changes the request for APIs that put it in the body (see ApiSourceConfig.Body).
        ctx.Provider?.Api?.Body is not null
            ? $"{ctx.Source.Type}|{ctx.Url}|{ctx.Keyword}"
            : $"{ctx.Source.Type}|{ctx.Url}";

    public static string HostOf(FetchContext ctx) =>
        Uri.TryCreate(ctx.Url, UriKind.Absolute, out var uri) ? uri.Host : "";

    /// <summary>
    /// Starts fetches for the upcoming sources (in the order given, current one first) until <c>degree</c> results
    /// are waiting to be consumed. Skips sources on a host that already has a request waiting.
    /// </summary>
    public void Prefetch(IEnumerable<FetchContext> upcoming)
    {
        foreach (var ctx in upcoming)
        {
            if (_unconsumedHosts.Count >= _degree) return;

            var key = KeyOf(ctx);
            if (_started.ContainsKey(key)) continue;

            var host = HostOf(ctx);
            if (host.Length > 0 && _unconsumedHosts.ContainsValue(host)) continue;

            Start(key, ctx);
        }
    }

    /// <summary>The fetch result for this source: already running or finished if it was prefetched, started now otherwise.</summary>
    public Task<IReadOnlyList<FetchedJob>> GetAsync(FetchContext ctx)
    {
        var key = KeyOf(ctx);
        var task = _started.TryGetValue(key, out var existing) ? existing : Start(key, ctx);
        _unconsumedHosts.Remove(key);
        return task;
    }

    /// <summary>
    /// The cycle decided not to use a prefetched result (its owner hit the limit or became unreachable meanwhile).
    /// Frees its place in the lookahead; the request itself finishes or is cancelled when the cycle ends.
    /// </summary>
    public void Forget(FetchContext? ctx)
    {
        if (ctx is not null) _unconsumedHosts.Remove(KeyOf(ctx));
    }

    private Task<IReadOnlyList<FetchedJob>> Start(string key, FetchContext ctx)
    {
        var token = _cts.Token;
        var task = Task.Run(() => FetchAsync(ctx, token));

        // A prefetched result nobody ends up asking for (the owner hit the limit meanwhile) must not leave an
        // unobserved exception behind.
        task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _started[key] = task;
        _unconsumedHosts[key] = HostOf(ctx);
        return task;
    }

    private async Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            return await _fetchers.Get(ctx.Source.Type).FetchAsync(ctx, ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Cancels whatever is still running and waits for it, so no request outlives the cycle.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_started.Values); }
        catch { /* every outcome was either consumed or no longer matters */ }

        _slots.Dispose();
        _cts.Dispose();
    }
}