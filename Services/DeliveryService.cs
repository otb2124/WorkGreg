using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Filtering;

namespace WorkGreg.Services;

/// <summary>Result of one delivery pass. <paramref name="Digest"/>: the pass was (or tried to be) a digest, not instant messages.</summary>
public readonly record struct DeliveryResult(int Sent, int Waiting, int Filtered, int Duplicates, bool Digest = false);

/// <summary>
/// Sends one owner's pending vacancies. Instant mode: one message per vacancy, up to the budget, saved per message so a
/// crash never causes duplicates. Digest mode: ONE message per day (see <see cref="DigestSchedule"/>) listing what
/// matches, sent in the final pass of a cycle after every source was fetched.
/// </summary>
public sealed class DeliveryService(
    INotifier notifier,
    FailureAlerter alerter,
    IOptions<PollOptions> options,
    TimeProvider time,
    ILogger<DeliveryService> log)
{
    /// <summary>
    /// <see cref="DeliverAsync"/> with the owner's problems contained: whatever goes wrong while talking to
    /// this one chat, the cycle goes on with the other owners.
    /// </summary>
    public async Task<DeliveryResult> DeliverSafelyAsync(
        AppDb db, long owner, int budget, int? onlySourceId, HashSet<long> unavailable, CancellationToken ct)
    {
        try
        {
            return await DeliverAsync(db, owner, budget, onlySourceId, ct);
        }
        catch (RecipientUnreachableException ex)
        {
            await alerter.DisableOwnerAsync(db, owner, ex.Message, unavailable, ct);
            return default;
        }
        catch (RateLimitedException)
        {
            log.LogWarning("Chat {Owner} is rate limited by Telegram; skipped until the next cycle", owner);
            unavailable.Add(owner);
            return default;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Delivery for chat {Owner} failed; skipped until the next cycle", owner);
            unavailable.Add(owner);
            return default;
        }
    }

    /// <summary>
    /// <paramref name="onlySourceId"/> limits the pass to one source's vacancies (null = all of them). With budget 0
    /// nothing is sent in instant mode: matching vacancies are just counted as waiting.
    /// </summary>
    private async Task<DeliveryResult> DeliverAsync(
        AppDb db, long owner, int budget, int? onlySourceId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FindAsync(new object[] { owner }, ct) ?? new UserSettings { ChatId = owner };
        if (settings.Paused) return default;

        if (settings.Delivery == DeliveryMode.Digest)
        {
            // A digest covers everything, so it is built once, in the final pass; never from one source's pass.
            var now = time.GetUtcNow().UtcDateTime;
            if (onlySourceId is not null || !DigestSchedule.IsDue(settings, now)) return default;

            return await DeliverDigestAsync(db, owner, settings, now, ct);
        }

        return await DeliverInstantAsync(db, owner, Math.Max(0, budget), onlySourceId, ct);
    }

    // ---- instant

    private async Task<DeliveryResult> DeliverInstantAsync(
        AppDb db, long owner, int budget, int? onlySourceId, CancellationToken ct)
    {
        var pending = await LoadPendingAsync(db, owner, onlySourceId, ct);

        var sent = 0;
        var waiting = 0;
        var filteredOut = 0;
        var duplicates = 0;

        foreach (var job in pending.Jobs)
        {
            var source = pending.Sources[job.SourceId];
            var profile = pending.Profiles[source.SearchProfileId];
            var criteria = pending.Criteria[profile.Id];

            if (!criteria.Matches(job))
            {
                job.Processed = true;
                filteredOut++;
                continue;
            }

            if (await AlreadyDeliveredAsync(db, owner, job.Hash, ct))
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

            try
            {
                var text = MessageFormatter.Vacancy(
                    job, source.Name, profile.Name, criteria, time.GetUtcNow().UtcDateTime);
                await notifier.SendAsync(owner, text, ct);
            }
            catch (MessageRejectedException ex)
            {
                // Telegram refuses this one message. Retrying every hour can't fix it, and one bad vacancy
                // must not block the ones behind it, so mark it done (not delivered) and move on.
                log.LogWarning("Telegram rejected vacancy '{Title}' for {Owner}: {Reason}; skipped", job.Title, owner, ex.Message);
                job.Processed = true;
                await db.SaveChangesAsync(ct);
                continue;
            }

            job.Processed = true;
            job.Delivered = true;
            sent++;

            await db.SaveChangesAsync(ct); // save per message so a crash never causes duplicates

            var gap = options.Value.SendDelayMilliseconds;
            if (gap > 0) await Task.Delay(TimeSpan.FromMilliseconds(gap), time, ct);
        }

        await db.SaveChangesAsync(ct);

        if (pending.Jobs.Count > 0)
            log.LogInformation(
                "Delivery for {Owner}: {Pending} pending, {Sent} sent, {Filtered} dropped by the profile's filters (Include/Exclude/Companies/Locations/Salary/Remote), {Duplicates} duplicates, {Waiting} waiting for the limit",
                owner, pending.Jobs.Count, sent, filteredOut, duplicates, waiting);

        return new DeliveryResult(sent, waiting, filteredOut, duplicates);
    }

    // ---- digest

    /// <summary>
    /// Filters the pending vacancies like instant mode does, then sends them as ONE message. Only when something
    /// matched is a message sent and <see cref="UserSettings.LastDigestSentUtc"/> set; with nothing to say the digest
    /// stays due, and goes out in the first cycle that has something. Vacancies that don't fit stay pending for the next digest.
    /// </summary>
    private async Task<DeliveryResult> DeliverDigestAsync(
        AppDb db, long owner, UserSettings settings, DateTime now, CancellationToken ct)
    {
        var pending = await LoadPendingAsync(db, owner, null, ct);

        var candidates = new List<(Job Job, DigestItem Item)>();
        var hashesInDigest = new HashSet<string>();
        var filteredOut = 0;
        var duplicates = 0;

        foreach (var job in pending.Jobs)
        {
            var source = pending.Sources[job.SourceId];
            var profile = pending.Profiles[source.SearchProfileId];

            if (!pending.Criteria[profile.Id].Matches(job))
            {
                job.Processed = true;
                filteredOut++;
                continue;
            }

            // Nothing is marked delivered until the digest is sent, so the same vacancy twice in this batch
            // (two boards, two profiles) has to be caught here as well as in the database.
            if (await AlreadyDeliveredAsync(db, owner, job.Hash, ct) || !hashesInDigest.Add(job.Hash))
            {
                job.Processed = true;
                duplicates++;
                continue;
            }

            candidates.Add((job, new DigestItem(job, source.Name, profile.Name)));
        }

        if (candidates.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return new DeliveryResult(0, 0, filteredOut, duplicates, Digest: true);
        }

        var page = MessageFormatter.Digest(candidates.Select(c => c.Item).ToList(), options.Value.DigestMaxItems);
        var included = candidates.Take(page.Included).Select(c => c.Job).ToList();

        try
        {
            await notifier.SendAsync(owner, page.Html, ct);
        }
        catch (MessageRejectedException ex)
        {
            // Same rule as for a single vacancy: retrying can't fix it, so these are done (not delivered) and
            // the digest stays due for whatever is left.
            log.LogWarning("Telegram rejected the digest for {Owner}: {Reason}; its {Count} vacancies are skipped",
                owner, ex.Message, included.Count);
            foreach (var job in included) job.Processed = true;
            await db.SaveChangesAsync(ct);
            return new DeliveryResult(0, candidates.Count - included.Count, filteredOut, duplicates, Digest: true);
        }

        foreach (var job in included)
        {
            job.Processed = true;
            job.Delivered = true;
        }
        settings.LastDigestSentUtc = now;
        await db.SaveChangesAsync(ct); // vacancies and the digest time in one save: a crash can't split them

        var left = candidates.Count - included.Count;
        log.LogInformation(
            "Digest for {Owner}: {Sent} vacancies in one message, {Left} left for the next digest, {Filtered} dropped by the profile's filters, {Duplicates} duplicates",
            owner, included.Count, left, filteredOut, duplicates);

        return new DeliveryResult(included.Count, left, filteredOut, duplicates, Digest: true);
    }

    // ---- shared

    private sealed record PendingJobs(
        IReadOnlyDictionary<int, Source> Sources,
        IReadOnlyDictionary<int, SearchProfile> Profiles,
        IReadOnlyDictionary<int, ProfileCriteria> Criteria,
        List<Job> Jobs);

    /// <summary>The owner's unprocessed vacancies (of one source, or all), newest first, for enabled profiles only.</summary>
    private static async Task<PendingJobs> LoadPendingAsync(AppDb db, long owner, int? onlySourceId, CancellationToken ct)
    {
        var sources = await db.Sources
            .Where(s => s.SearchProfile.OwnerChatId == owner)
            .ToDictionaryAsync(s => s.Id, ct);

        // Only enabled profiles deliver. Jobs of a disabled profile stay pending until it is enabled again.
        var profiles = await db.SearchProfiles
            .Where(p => p.OwnerChatId == owner && p.Enabled)
            .ToDictionaryAsync(p => p.Id, ct);

        var criteria = profiles.ToDictionary(p => p.Key, p => ProfileCriteria.For(p.Value));

        var jobs = (await db.Jobs
                .Where(j => j.OwnerChatId == owner && !j.Processed && (onlySourceId == null || j.SourceId == onlySourceId))
                .OrderByDescending(j => j.PostedUtc ?? j.FirstSeenUtc)
                .ToListAsync(ct))
            .Where(j => sources.TryGetValue(j.SourceId, out var s) && profiles.ContainsKey(s.SearchProfileId))
            .ToList();

        return new PendingJobs(sources, profiles, criteria, jobs);
    }

    private static Task<bool> AlreadyDeliveredAsync(AppDb db, long owner, string hash, CancellationToken ct) =>
        db.Jobs.AnyAsync(j => j.OwnerChatId == owner && j.Hash == hash && j.Delivered, ct);
}