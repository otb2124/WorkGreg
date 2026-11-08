using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Filtering;

namespace WorkGreg.Services;

/// <summary>Result of one delivery pass.</summary>
public readonly record struct DeliveryResult(int Sent, int Waiting, int Filtered, int Duplicates);

/// <summary>
/// Sends one owner's pending vacancies: applies the profile's criteria, skips duplicates, respects the budget,
/// and saves state per message so a crash never causes duplicates.
/// </summary>
public sealed class DeliveryService(
    INotifier notifier,
    FailureAlerter alerter,
    IOptions<PollOptions> options,
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
    /// Filters the owner's pending vacancies (only those of <paramref name="onlySourceId"/>, or all of them when null)
    /// and sends at most <paramref name="budget"/> of them, newest first. With budget 0 nothing is sent: matching
    /// vacancies are just counted as waiting.
    /// </summary>
    private async Task<DeliveryResult> DeliverAsync(
        AppDb db, long owner, int budget, int? onlySourceId, CancellationToken ct)
    {
        if (budget < 0) budget = 0;

        var settings = await db.UserSettings.FindAsync(new object[] { owner }, ct) ?? new UserSettings { ChatId = owner };
        if (settings.Paused) return default;

        // Digest mode: only deliver during the configured hour (UTC).
        if (settings.Delivery == DeliveryMode.Digest && DateTime.UtcNow.Hour != settings.DigestHourUtc)
            return default;

        var sources = await db.Sources
            .Where(s => s.SearchProfile.OwnerChatId == owner)
            .ToDictionaryAsync(s => s.Id, ct);

        // Only enabled profiles deliver. Jobs of a disabled profile stay pending until it is enabled again.
        var profiles = await db.SearchProfiles
            .Where(p => p.OwnerChatId == owner && p.Enabled)
            .ToDictionaryAsync(p => p.Id, ct);

        var criteria = profiles.ToDictionary(p => p.Key, p => ProfileCriteria.For(p.Value));

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
            var profileCriteria = criteria[profile.Id];

            if (!profileCriteria.Matches(job))
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

            try
            {
                await notifier.SendAsync(owner, MessageFormatter.Vacancy(job, source.Name, profile.Name, profileCriteria), ct);
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
            if (gap > 0) await Task.Delay(gap, ct);
        }

        await db.SaveChangesAsync(ct);

        if (pending.Count > 0)
            log.LogInformation(
                "Delivery for {Owner}: {Pending} pending, {Sent} sent, {Filtered} dropped by the profile's filters (Include/Exclude/Companies/Locations/Salary/Remote), {Duplicates} duplicates, {Waiting} waiting for the limit",
                owner, pending.Count, sent, filteredOut, duplicates, waiting);

        return new DeliveryResult(sent, waiting, filteredOut, duplicates);
    }
}