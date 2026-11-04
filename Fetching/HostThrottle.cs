using System.Collections.Concurrent;

namespace WorkGreg.Fetching;

/// <summary>
/// Politeness: requests to the same host run one at a time with a gap between them.
/// Every fetcher sends its HTTP request through <see cref="RunAsync{T}"/>.
/// </summary>
public sealed class HostThrottle
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
            var wait = state.NextAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            try { return await action(); }
            finally { state.NextAllowedUtc = DateTime.UtcNow + MinGap; }
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
