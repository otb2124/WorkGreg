namespace WorkGreg.Fetching;

/// <summary>
/// Remembers, per feed URL, the validators the server sent (ETag / Last-Modified) together with the vacancies parsed
/// from that response. On a "304 Not Modified" <see cref="FeedFetcher"/> returns the remembered vacancies, so every source
/// still gets the full list (a brand-new source of the same URL, or a source whose last save failed, loses nothing),
/// but the site is asked for nothing and nothing is parsed.
/// In memory on purpose: after a restart the first fetch is simply a normal full one, so no database change is needed.
/// </summary>
public sealed class FeedCache(TimeProvider time)
{
    public sealed record Entry(string? ETag, string? LastModified, IReadOnlyList<FetchedJob> Items);

    /// <summary>Keyword changes create new URLs; the least recently used entries are dropped past this size.</summary>
    private const int MaxEntries = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, (Entry Entry, DateTime LastUsedUtc)> _entries = new(StringComparer.Ordinal);

    public Entry? Get(string url)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(url, out var found)) return null;

            _entries[url] = (found.Entry, Now());
            return found.Entry;
        }
    }

    public void Set(string url, Entry entry)
    {
        lock (_gate)
        {
            _entries[url] = (entry, Now());

            if (_entries.Count > MaxEntries)
                _entries.Remove(_entries.MinBy(e => e.Value.LastUsedUtc).Key);
        }
    }

    public void Remove(string url)
    {
        lock (_gate) _entries.Remove(url);
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}