using System.Collections.Concurrent;

namespace WorkGreg.Fetching;

/// <summary>
/// Politeness: requests to the same host run one at a time with a gap between them. Requests to different hosts
/// don't wait for each other, so the caller may fetch several hosts in parallel (see <see cref="FetchPipeline"/>).
/// Every fetcher sends its HTTP request through <see cref="RunAsync{T}"/>.
/// </summary>
public sealed class HostThrottle(TimeProvider time)
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

    private sealed class HostState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTime NextAllowedUtc = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> RunAsync<T>(string url, Func<Task<T>> action, CancellationToken ct)
    {
        var state = _hosts.GetOrAdd(new Uri(url).Host, _ => new HostState());

        await state.Gate.WaitAsync(ct);
        try
        {
            var wait = state.NextAllowedUtc - Now();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);

            try { return await action(); }
            finally { state.NextAllowedUtc = Now() + MinGap; }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}