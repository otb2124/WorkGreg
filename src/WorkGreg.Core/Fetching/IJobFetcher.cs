using WorkGreg.Domain;

namespace WorkGreg.Fetching;

/// <summary>A normalized vacancy as returned by any fetcher.</summary>
public sealed record FetchedJob(
    string ExternalId,
    string Title,
    string? Company,
    string? Location,
    string Url,
    string? Description,
    DateTime? PostedUtc,
    int? Salary = null,
    string? EmploymentType = null);

/// <summary>
/// Everything a fetcher needs for one search. Keyword comes from the source's SearchProfile. Provider is null for legacy sources added with a raw URL.
/// Site-specific settings (selectors, JSON paths) come from Provider, not from the database.
/// </summary>
public sealed record FetchContext(Source Source, SourceProvider? Provider, string Url, string Keyword);

/// <summary>
/// One implementation per <see cref="FetchType"/>. To add a new strategy:
/// implement this, register it in Program.cs. Nothing else changes.
/// </summary>
public interface IJobFetcher
{
    FetchType Type { get; }

    /// <summary>False for planned-but-not-built strategies; the bot refuses to create sources of that type.</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<FetchedJob>> FetchAsync(FetchContext ctx, CancellationToken ct);
}

public sealed class FetcherRegistry(IEnumerable<IJobFetcher> fetchers)
{
    private readonly Dictionary<FetchType, IJobFetcher> _byType = fetchers.ToDictionary(f => f.Type);

    public IJobFetcher Get(FetchType type) =>
        _byType.TryGetValue(type, out var f)
            ? f
            : throw new NotSupportedException($"No fetcher registered for '{type}'.");

    public IEnumerable<IJobFetcher> All => _byType.Values;
}
