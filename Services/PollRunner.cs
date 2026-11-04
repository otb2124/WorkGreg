using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

/// <summary>
/// One poll cycle. Sources are fetched ONE AT A TIME and each source's new vacancies are filtered and sent
/// right after it is fetched. Once an owner has received MaxMessagesPerRun vacancies, none of their remaining
/// sources are fetched in this cycle. Safe to call concurrently (calls are serialized) and safe to crash mid-way
/// (state is saved per job).
/// </summary>
public sealed partial class PollRunner(
    IServiceScopeFactory scopes,
    FetcherRegistry fetchers,
    INotifier notifier,
    IOptions<PollOptions> options,
    ILogger<PollRunner> log)
{
    private const int FailureAlertThreshold = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stopLock = new();
    private CancellationTokenSource? _runCts;
    private bool _stopRequested;

    /// <summary>True while a poll cycle is executing.</summary>
    public bool IsRunning => Volatile.Read(ref _runCts) is not null;

    /// <summary>True if the most recent cycle ended because <see cref="RequestStop"/> was called.</summary>
    public bool LastRunStopped { get; private set; }

    /// <summary>Cancels the cycle in progress. Returns false if none is running.</summary>
    public bool RequestStop()
    {
        lock (_stopLock)
        {
            if (_runCts is null) return false;
            _stopRequested = true;
            _runCts.Cancel();
            return true;
        }
    }

    /// <summary>Result of one delivery pass.</summary>
    private readonly record struct Delivery(int Sent, int Waiting, int Filtered, int Duplicates);

    /// <param name="ownerChatId">Null = all users (scheduled run); otherwise only that user's profiles.</param>
    /// <returns>Number of vacancies sent.</returns>
    public async Task<int> RunAsync(long? ownerChatId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_stopLock)
        {
            _stopRequested = false;
            LastRunStopped = false;
            _runCts = runCts;
        }

        var sentBy = new Dictionary<long, int>();
        try
        {
            var runToken = runCts.Token;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();

            var profiles = (await db.SearchProfiles
                    .Where(p => p.Enabled && (ownerChatId == null || p.OwnerChatId == ownerChatId))
                    .ToListAsync(runToken))
                .ToDictionary(p => p.Id);

            var profileIds = profiles.Keys.ToList();
            var sources = await db.Sources
                .Where(s => s.Enabled && profileIds.Contains(s.SearchProfileId))
                .ToListAsync(runToken);

            var max = options.Value.MaxMessagesPerRun;
            var owners = sources.Select(s => s.OwnerChatId).Distinct().ToList();
            sentBy = owners.ToDictionary(o => o, _ => 0);
            var waitingBy = owners.ToDictionary(o => o, _ => 0);
            var skippedBy = owners.ToDictionary(o => o, _ => 0);
            var filteredBy = owners.ToDictionary(o => o, _ => 0);

            // Raw fetch results by (type, url) for this cycle: searches that share a URL cost one request
            // (e.g. two profiles that both use Remotive, whose URL doesn't depend on the keyword).
            var cache = new Dictionary<string, IReadOnlyList<FetchedJob>>();

            // Least recently fetched first: when the limit ends a cycle early, the sources that
            // didn't get a turn are the first ones to be fetched next time (no source starves).
            var queue = sources.OrderBy(s => s.LastSuccessUtc ?? DateTime.MinValue).ThenBy(s => s.Id).ToList();

            var fetched = 0;
            foreach (var source in queue)
            {
                runToken.ThrowIfCancellationRequested();

                var owner = source.OwnerChatId;
                if (sentBy[owner] >= max)
                {
                    skippedBy[owner]++;
                    continue;
                }

                await FetchSourceAsync(db, source, profiles[source.SearchProfileId], cache, runToken);
                fetched++;

                // Send what THIS source just found, right now (newest first). Older leftovers don't jump the queue.
                var d = await DeliverAsync(db, owner, max - sentBy[owner], source.Id, runToken);
                sentBy[owner] += d.Sent;
                filteredBy[owner] += d.Filtered;

                // Everybody is full: stop fetching.
                if (owners.All(o => sentBy[o] >= max)) break;
            }

            // Leftovers from earlier runs go out only if there is room, newest first. With no room this
            // just counts what is waiting.
            foreach (var owner in owners)
            {
                var d = await DeliverAsync(db, owner, max - sentBy[owner], null, runToken);
                sentBy[owner] += d.Sent;
                waitingBy[owner] = d.Waiting;
                filteredBy[owner] += d.Filtered;
            }

            foreach (var owner in owners)
            {
                var skipped = skippedBy[owner];
                if (sentBy[owner] >= max && (waitingBy[owner] > 0 || skipped > 0))
                    await SafeSendAsync(owner,
                        $"… limit of {max} reached for this run" +
                        (waitingBy[owner] > 0 ? $", {waitingBy[owner]} more matching vacancies are waiting" : "") +
                        (skipped > 0 ? $", {skipped} sources were not checked yet" : "") +
                        ". They will arrive on the next run.", runToken);
                else if (ownerChatId is not null && sentBy[owner] == 0 && filteredBy[owner] > 0)
                    // Manual /check found keyword matches but the profile's filters removed all of them.
                    await SafeSendAsync(owner,
                        $"ℹ️ {filteredBy[owner]} vacancies matched your keyword but were dropped by your profile filters " +
                        "(Include, Exclude, Companies, Locations, Min salary, Remote only). Open 🎯 Filters to check them.", runToken);
            }

            var sent = sentBy.Values.Sum();
            log.LogInformation("Poll finished: {Fetched}/{Sources} sources checked, {Sent} vacancies sent (limit {Max} per user)",
                fetched, sources.Count, sent, max);
            return sent;
        }
        catch (OperationCanceledException) when (_stopRequested && !ct.IsCancellationRequested)
        {
            // /stop (or the Stop button): not an error. Everything delivered so far is already saved.
            LastRunStopped = true;
            var sent = sentBy.Values.Sum();
            log.LogInformation("Poll stopped by user: {Sent} vacancies sent before the stop", sent);
            return sent;
        }
        finally
        {
            lock (_stopLock) { _runCts = null; }
            _gate.Release();
        }
    }

    private async Task FetchSourceAsync(
        AppDb db, Source source, SearchProfile profile, Dictionary<string, IReadOnlyList<FetchedJob>> cache, CancellationToken ct)
    {
        try
        {
            var provider = SourceCatalog.Find(source.Provider);

            // A catalog source that was removed from SourceCatalog (dead feed, wrong token...). Don't hit its stale URL.
            if (source.Provider is not null && provider is null)
            {
                if (source.LastError is null)
                {
                    source.LastError = "This source was removed from the catalog.";
                    await db.SaveChangesAsync(ct);
                }
                log.LogWarning("Source {Id} '{Name}' uses provider '{Provider}' which is no longer in the catalog; skipped", source.Id, source.Name, source.Provider);
                return;
            }

            // Derived from the profile's current keyword, so a stale stored Url can never be fetched.
            var url = provider is not null ? provider.BuildUrl(profile.Keyword) : source.Url;
            var cacheKey = $"{source.Type}|{url}";

            if (!cache.TryGetValue(cacheKey, out var items))
            {
                items = await fetchers.Get(source.Type).FetchAsync(new FetchContext(source, provider, url, profile.Keyword), ct);
                cache[cacheKey] = items;
            }

            // A scraper whose selectors broke returns nothing and still "succeeds". Count that as a failure
            // so the usual 3-in-a-row alert fires. Feeds are excluded: an empty search feed is normal.
            if (items.Count == 0 && source.Type != FetchType.Feed && source.LastSuccessUtc is not null &&
                await db.Jobs.AnyAsync(j => j.SourceId == source.Id, ct))
                throw new InvalidOperationException(
                    "The source returned 0 items although it returned vacancies before; the site or its response format probably changed.");

            // The provider's own search is a hint at best; the keyword is always enforced here.
            if (!string.IsNullOrWhiteSpace(profile.Keyword))
                items = items
                    .Where(i => SourceCatalog.MatchesKeyword(profile.Keyword, i.Title, i.Description))
                    .ToList();

            var known = (await db.Jobs
                    .Where(j => j.SourceId == source.Id)
                    .Select(j => j.ExternalId)
                    .ToListAsync(ct))
                .ToHashSet();

            var now = DateTime.UtcNow;
            var added = 0;

            foreach (var item in items)
            {
                if (!known.Add(item.ExternalId)) continue;

                db.Jobs.Add(new Job
                {
                    SourceId = source.Id,
                    OwnerChatId = source.OwnerChatId,
                    ExternalId = item.ExternalId,
                    Hash = ComputeHash(item),
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

            source.Baselined = true;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
            source.LastSuccessUtc = now;
            await db.SaveChangesAsync(ct);

            log.LogInformation("Source {Id} '{Name}' (profile '{Profile}'): {Total} items match the keyword, {Added} new", source.Id, source.Name, profile.Name, items.Count, added);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Source {Id} '{Name}' (profile '{Profile}') failed: {Error}", source.Id, source.Name, profile.Name, ex.Message);

            source.ConsecutiveFailures++;
            source.LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            await db.SaveChangesAsync(ct);

            // Usually means the site changed or blocked us. Tell the owner once, not every hour.
            if (source.ConsecutiveFailures == FailureAlertThreshold)
            {
                await SafeSendAsync(source.OwnerChatId,
                    $"⚠️ <b>{Html(source.Name)}</b> in profile <b>{Html(profile.Name)}</b> failed {FailureAlertThreshold} times in a row:\n<code>{Html(source.LastError)}</code>",
                    ct);
            }
        }
    }

    /// <summary>
    /// Filters the owner's pending vacancies (only those of <paramref name="onlySourceId"/>, or all of them when null)
    /// and sends at most <paramref name="budget"/> of them, newest first. With budget 0 nothing is sent: matching
    /// vacancies are just counted as waiting.
    /// </summary>
    private async Task<Delivery> DeliverAsync(AppDb db, long owner, int budget, int? onlySourceId, CancellationToken ct)
    {
        if (budget < 0) budget = 0;

        var settings = await db.Profiles.FindAsync(new object[] { owner }, ct) ?? new UserProfile { ChatId = owner };
        if (settings.Paused) return new Delivery(0, 0, 0, 0);

        // Digest mode: only deliver during the configured hour (UTC).
        if (settings.Delivery == DeliveryMode.Digest && DateTime.UtcNow.Hour != settings.DigestHourUtc)
            return new Delivery(0, 0, 0, 0);

        var sources = await db.Sources
            .Where(s => s.OwnerChatId == owner)
            .ToDictionaryAsync(s => s.Id, ct);

        // Only enabled profiles deliver. Jobs of a disabled profile stay pending until it is enabled again.
        var profiles = await db.SearchProfiles
            .Where(p => p.OwnerChatId == owner && p.Enabled)
            .ToDictionaryAsync(p => p.Id, ct);

        var pending = (await db.Jobs
                .Where(j => j.OwnerChatId == owner && !j.Processed && (onlySourceId == null || j.SourceId == onlySourceId))
                .OrderByDescending(j => j.PostedUtc ?? j.FirstSeenUtc)
                .ToListAsync(ct))
            .Where(j => sources.TryGetValue(j.SourceId, out var s) && profiles.ContainsKey(s.SearchProfileId))
            .ToList();

        var sent = 0;
        var waiting = 0;
        var filteredOut = 0;
        var duplicates = 0;

        foreach (var job in pending)
        {
            var source = sources[job.SourceId];
            var profile = profiles[source.SearchProfileId];

            if (!FilterEngine.Matches(job, profile))
            {
                job.Processed = true;
                filteredOut++;
                continue;
            }

            var duplicate = await db.Jobs.AnyAsync(
                j => j.OwnerChatId == owner && j.Hash == job.Hash && j.Delivered, ct);
            if (duplicate)
            {
                job.Processed = true;
                duplicates++;
                continue;
            }

            // Limit reached: keep it pending for the next run, but keep going so the remaining ones are filtered and counted.
            if (sent >= budget)
            {
                waiting++;
                continue;
            }

            await notifier.SendAsync(owner, FormatMessage(job, source.Name, profile.Name), ct);
            job.Processed = true;
            job.Delivered = true;
            sent++;

            await db.SaveChangesAsync(ct); // save per message so a crash never causes duplicates
            await Task.Delay(100, ct);
        }

        await db.SaveChangesAsync(ct);

        if (pending.Count > 0)
            log.LogInformation(
                "Delivery for {Owner}: {Pending} pending, {Sent} sent, {Filtered} dropped by the profile's filters (Include/Exclude/Companies/Locations/Salary/Remote), {Duplicates} duplicates, {Waiting} waiting for the limit",
                owner, pending.Count, sent, filteredOut, duplicates, waiting);

        return new Delivery(sent, waiting, filteredOut, duplicates);
    }

    private static string FormatMessage(Job job, string sourceName, string profileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(job.Title)}</b>");

        var meta = string.Join(" · ", new[] { job.Company, job.Location }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (meta.Length > 0) sb.AppendLine(Html(meta));

        sb.AppendLine($"<i>{Html(profileName)} · {Html(sourceName)}</i>");
        sb.Append($"<a href=\"{WebUtility.HtmlEncode(job.Url)}\">Open vacancy</a>");
        return sb.ToString();
    }

    private async Task SafeSendAsync(long chatId, string html, CancellationToken ct)
    {
        try { await notifier.SendAsync(chatId, html, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Failed to send message to {ChatId}", chatId);
        }
    }

    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string ComputeHash(FetchedJob job)
    {
        var normalized = NonAlnum().Replace($"{job.Company}{job.Title}".ToLowerInvariant(), "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]")] private static partial Regex NonAlnum();
}