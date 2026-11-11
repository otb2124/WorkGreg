using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Filtering;

namespace WorkGreg.Services;

/// <summary>Result of one delivery pass. <paramref name="Digest"/>: the pass was (or tried to be) a digest, not instant messages.</summary>
public readonly record struct DeliveryResult(int Sent, int Waiting, int Filtered, int Duplicates, bool Digest = false);

/// <summary>
/// Delivery side of the cycle: sends one owner's pending vacancies. It reads and writes only that owner's
/// <see cref="JobDelivery"/> rows (the shared <see cref="Job"/> is never modified here). Instant mode: one message per vacancy, up to the budget, saved per message so a
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
    // Keeps the IN (...) lookup well under SQLite's parameter limits (same size SourceSyncService uses).
    private const int HashLookupBatchSize = 500;

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
        var loadStarted = time.GetTimestamp();
        var pending = await LoadPendingAsync(db, owner, onlySourceId, ct);
        var loadTime = time.GetElapsedTime(loadStarted);

        var sent = 0;
        var waiting = 0;
        var filteredOut = 0;
        var duplicates = 0;

        foreach (var delivery in pending.Deliveries)
        {
            var job = delivery.Job;
            var (verdict, source, profile, criteria) = Classify(pending, delivery, null);

            if (verdict == Verdict.Filtered) { filteredOut++; continue; }
            if (verdict == Verdict.Duplicate) { duplicates++; continue; }

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
                delivery.Processed = true;
                await db.SaveChangesAsync(ct);
                continue;
            }

            delivery.Processed = true;
            delivery.Delivered = true;
            sent++;

            // The same vacancy later in this pass (another board, another profile) is now a duplicate. The database
            // is not asked again: the set loaded at the start of the pass is kept up to date here instead.
            pending.DeliveredHashes.Add(job.Hash);

            await db.SaveChangesAsync(ct); // save per message so a crash never causes duplicates

            var gap = options.Value.SendDelayMilliseconds;
            if (gap > 0) await Task.Delay(TimeSpan.FromMilliseconds(gap), time, ct);
        }

        await db.SaveChangesAsync(ct);

        if (pending.Deliveries.Count > 0)
            log.LogInformation(
                "Delivery for {Owner}: {Pending} pending (loaded in {LoadMs:0} ms), {Sent} sent, {Filtered} dropped by the profile's filters (Include/Exclude/Companies/Locations/Salary/Remote), {Duplicates} duplicates, {Waiting} waiting for the limit",
                owner, pending.Deliveries.Count, loadTime.TotalMilliseconds, sent, filteredOut, duplicates, waiting);

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

        var candidates = new List<(JobDelivery Delivery, DigestItem Item)>();
        var hashesInDigest = new HashSet<string>();
        var filteredOut = 0;
        var duplicates = 0;

        foreach (var delivery in pending.Deliveries)
        {
            // Nothing is marked delivered until the digest is sent, so the same vacancy twice in this batch
            // (two boards, two profiles) has to be caught here as well as against what was sent before.
            var (verdict, source, profile, _) = Classify(pending, delivery, hashesInDigest);

            if (verdict == Verdict.Filtered) { filteredOut++; continue; }
            if (verdict == Verdict.Duplicate) { duplicates++; continue; }

            candidates.Add((delivery, new DigestItem(delivery.Job, source.Name, profile.Name)));
        }

        if (candidates.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return new DeliveryResult(0, 0, filteredOut, duplicates, Digest: true);
        }

        var page = MessageFormatter.Digest(candidates.Select(c => c.Item).ToList(), options.Value.DigestMaxItems);
        var included = candidates.Take(page.Included).Select(c => c.Delivery).ToList();

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
            foreach (var delivery in included) delivery.Processed = true;
            await db.SaveChangesAsync(ct);
            return new DeliveryResult(0, candidates.Count - included.Count, filteredOut, duplicates, Digest: true);
        }

        foreach (var delivery in included)
        {
            delivery.Processed = true;
            delivery.Delivered = true;
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

    /// <param name="DeliveredHashes">
    /// Hashes (company + title, on any board or profile) this owner has already been sent, for the vacancies in
    /// <paramref name="Deliveries"/>. Loaded once per pass; instant mode adds to it as it sends.
    /// </param>
    private sealed record PendingJobs(
        IReadOnlyDictionary<int, Source> Sources,
        IReadOnlyDictionary<int, SearchProfile> Profiles,
        IReadOnlyDictionary<int, ProfileCriteria> Criteria,
        List<JobDelivery> Deliveries,
        HashSet<string> DeliveredHashes);

    /// <summary>
    /// The owner's unprocessed deliveries (of one source, or all) with their vacancies, newest first, for enabled profiles only.
    /// "Newest" is the posting date, or when this owner's source first stored it for undated postings.
    /// Also loads, in the same step, which of those vacancies this owner already received (see <see cref="LoadDeliveredHashesAsync"/>).
    /// </summary>
    private static async Task<PendingJobs> LoadPendingAsync(AppDb db, long owner, int? onlySourceId, CancellationToken ct)
    {
        var sources = await db.Sources
            .Where(s => s.SearchProfile.OwnerChatId == owner)
            .ToDictionaryAsync(s => s.Id, ct);

        // Only enabled profiles deliver. Deliveries of a disabled profile stay pending until it is enabled again.
        var profiles = await db.SearchProfiles
            .Where(p => p.OwnerChatId == owner && p.Enabled)
            .ToDictionaryAsync(p => p.Id, ct);

        var criteria = profiles.ToDictionary(p => p.Key, p => ProfileCriteria.For(p.Value));

        var deliveries = (await db.JobDeliveries
                .Include(d => d.Job)
                .Where(d => d.OwnerChatId == owner && !d.Processed && (onlySourceId == null || d.SourceId == onlySourceId))
                .OrderByDescending(d => d.Job.PostedUtc ?? d.CreatedUtc)
                .ToListAsync(ct))
            .Where(d => sources.TryGetValue(d.SourceId, out var s) && profiles.ContainsKey(s.SearchProfileId))
            .ToList();

        var delivered = await LoadDeliveredHashesAsync(db, owner, deliveries.Select(d => d.Job.Hash), ct);

        return new PendingJobs(sources, profiles, criteria, deliveries, delivered);
    }

    /// <summary>
    /// Which of these hashes were already sent to this owner (same company + title, on any board or profile)?
    /// One <c>Hash IN (...)</c> query per chunk of <see cref="HashLookupBatchSize"/>, instead of one query per vacancy.
    /// </summary>
    private static async Task<HashSet<string>> LoadDeliveredHashesAsync(
        AppDb db, long owner, IEnumerable<string> hashes, CancellationToken ct)
    {
        var delivered = new HashSet<string>();

        foreach (var batch in hashes.Distinct().Chunk(HashLookupBatchSize))
        {
            var found = await db.JobDeliveries
                .Where(d => d.OwnerChatId == owner && d.Delivered && batch.Contains(d.Job.Hash))
                .Select(d => d.Job.Hash)
                .Distinct()
                .ToListAsync(ct);

            delivered.UnionWith(found);
        }

        return delivered;
    }

    private enum Verdict { Deliver, Filtered, Duplicate }

    private readonly record struct Classified(Verdict Verdict, Source Source, SearchProfile Profile, ProfileCriteria Criteria);

    /// <summary>
    /// The step instant and digest delivery have in common: does this vacancy match its profile's filters, and was it
    /// already sent? A vacancy that is filtered out or a duplicate is marked processed here (nothing more to do with it);
    /// a <see cref="Verdict.Deliver"/> one is left alone for the caller to send or keep waiting.
    /// "Already sent" is answered from <see cref="PendingJobs.DeliveredHashes"/>, with no database call.
    /// <paramref name="seenInBatch"/> is only for a mode that sends many vacancies in one message (the digest): a hash
    /// already in it counts as a duplicate too. Instant mode passes null and keeps <see cref="PendingJobs.DeliveredHashes"/>
    /// current as it sends each message.
    /// </summary>
    private static Classified Classify(PendingJobs pending, JobDelivery delivery, ISet<string>? seenInBatch)
    {
        var job = delivery.Job;
        var source = pending.Sources[delivery.SourceId];
        var profile = pending.Profiles[source.SearchProfileId];
        var criteria = pending.Criteria[profile.Id];

        if (!criteria.Matches(job))
        {
            delivery.Processed = true;
            return new Classified(Verdict.Filtered, source, profile, criteria);
        }

        if (pending.DeliveredHashes.Contains(job.Hash) || (seenInBatch is not null && !seenInBatch.Add(job.Hash)))
        {
            delivery.Processed = true;
            return new Classified(Verdict.Duplicate, source, profile, criteria);
        }

        return new Classified(Verdict.Deliver, source, profile, criteria);
    }
}