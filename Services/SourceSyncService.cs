using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;

namespace WorkGreg.Services;

/// <summary>
/// Brings one source up to date: take the fetched vacancies, keep what matches the profile's keyword, store what is new.
/// The network request itself comes from the cycle's <see cref="FetchPipeline"/> (possibly already finished).
/// Never throws for a source problem: it is recorded on the source (and alerted) and the cycle goes on.
/// </summary>
public sealed class SourceSyncService(
    FailureAlerter alerter,
    IOptions<RetentionOptions> retention,
    TimeProvider time,
    ILogger<SourceSyncService> log)
{
    /// <summary>
    /// What to fetch for this source, or null when its catalog provider was removed (dead feed, wrong token...)
    /// and its stale URL must not be hit. Pure: no I/O, so the cycle can plan ahead.
    /// </summary>
    public FetchContext? Plan(Source source, SearchProfile profile)
    {
        var provider = SourceCatalog.Find(source.Provider);
        if (source.Provider is not null && provider is null) return null;

        // Derived from the profile's current keyword, so a stale stored Url can never be fetched.
        var url = provider is not null ? provider.BuildUrl(profile.Keyword) : source.Url;
        return new FetchContext(source, provider, url, profile.Keyword);
    }

    public async Task SyncAsync(
        AppDb db, Source source, SearchProfile profile, FetchPipeline fetches,
        HashSet<long> unavailable, CancellationToken ct)
    {
        try
        {
            var plan = Plan(source, profile);
            if (plan is null)
            {
                await MarkRemovedAsync(db, source, ct);
                return;
            }

            var items = await FetchAsync(db, plan, fetches, ct);
            items = KeepKeywordMatches(items, profile);

            var added = await StoreNewAsync(db, source, profile, items, ct);

            log.LogInformation(
                "Source {Id} '{Name}' (profile '{Profile}'): {Total} items match the keyword, {Added} new",
                source.Id, source.Name, profile.Name, items.Count, added);
        }
        // "!ct.IsCancellationRequested" instead of "is not OperationCanceledException": an HttpClient timeout
        // is reported as a TaskCanceledException even though nobody cancelled us. That is an ordinary source
        // failure, not a stop request, and must not abort the whole cycle.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(db, source, profile, ex, unavailable, ct);
        }
    }

    private async Task MarkRemovedAsync(AppDb db, Source source, CancellationToken ct)
    {
        if (source.LastError is null)
        {
            source.LastError = "This source was removed from the catalog.";
            await db.SaveChangesAsync(ct);
        }

        log.LogWarning(
            "Source {Id} '{Name}' uses provider '{Provider}' which is no longer in the catalog; skipped",
            source.Id, source.Name, source.Provider);
    }

    private static async Task<IReadOnlyList<FetchedJob>> FetchAsync(
        AppDb db, FetchContext plan, FetchPipeline fetches, CancellationToken ct)
    {
        var items = await fetches.GetAsync(plan).WaitAsync(ct);
        var source = plan.Source;

        // A scraper whose selectors broke returns nothing and still "succeeds". Count that as a failure
        // so the usual 3-in-a-row alert fires. Feeds are excluded: an empty search feed is normal.
        if (items.Count == 0 && source.Type != FetchType.Feed && source.LastSuccessUtc is not null &&
            await db.Jobs.AnyAsync(j => j.SourceId == source.Id, ct))
            throw new InvalidOperationException(
                "The source returned 0 items although it returned vacancies before; the site or its response format probably changed.");

        return items;
    }

    /// <summary>The provider's own search is a hint at best; the keyword is always enforced here.</summary>
    private static IReadOnlyList<FetchedJob> KeepKeywordMatches(IReadOnlyList<FetchedJob> items, SearchProfile profile)
    {
        var keyword = ProfileCriteria.KeywordOf(profile);
        return keyword is null
            ? items
            : items.Where(i => keyword.Matches(i.Title, i.Description)).ToList();
    }

    /// <summary>Stores vacancies not seen before for this source and records the successful sync. Returns how many were new.</summary>
    private async Task<int> StoreNewAsync(
        AppDb db, Source source, SearchProfile profile, IReadOnlyList<FetchedJob> items, CancellationToken ct)
    {
        var known = (await db.Jobs
                .Where(j => j.SourceId == source.Id)
                .Select(j => j.ExternalId)
                .ToListAsync(ct))
            .ToHashSet();

        // Scope for vacancies without a company, see JobHasher.
        var hashScope = source.Provider ?? $"source{source.Id}";

        var now = time.GetUtcNow().UtcDateTime;

        // Anything posted before the retention window would be deleted again by the retention job and then come
        // back as "new" on the next fetch, so it is not stored at all. (Postings without a date can't be told apart.)
        var oldestAllowed = retention.Value.CutoffUtc(now);

        var added = 0;

        foreach (var item in items)
        {
            if (oldestAllowed is { } cutoff && item.PostedUtc is { } posted && posted < cutoff) continue;
            if (!known.Add(item.ExternalId)) continue;

            db.Jobs.Add(new Job
            {
                SourceId = source.Id,
                OwnerChatId = profile.OwnerChatId,
                ExternalId = item.ExternalId,
                Hash = JobHasher.Compute(item.Company, item.Title, hashScope),
                Title = item.Title,
                Company = item.Company,
                Location = item.Location,
                Url = item.Url,
                Description = item.Description,
                PostedUtc = item.PostedUtc,
                Salary = item.Salary,
                EmploymentType = item.EmploymentType,
                FirstSeenUtc = now,
                Processed = false
            });
            added++;
        }

        source.ConsecutiveFailures = 0;
        source.LastError = null;
        source.LastSuccessUtc = now;
        await db.SaveChangesAsync(ct);

        return added;
    }

    private async Task RecordFailureAsync(
        AppDb db, Source source, SearchProfile profile, Exception ex, HashSet<long> unavailable, CancellationToken ct)
    {
        log.LogWarning("Source {Id} '{Name}' (profile '{Profile}') failed: {Error}",
            source.Id, source.Name, profile.Name, ex.Message);

        source.ConsecutiveFailures++;
        source.LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
        await db.SaveChangesAsync(ct);

        await alerter.SourceFailedAsync(db, source, profile, unavailable, ct);
    }
}