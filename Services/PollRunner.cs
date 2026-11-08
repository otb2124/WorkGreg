using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;

namespace WorkGreg.Services;

/// <summary>Outcome of one poll cycle.</summary>
public readonly record struct PollResult(int Sent, bool Stopped);

/// <summary>
/// One poll cycle: decides the ORDER of work and keeps the per-owner counters. Sources are handled ONE AT A TIME
/// (<see cref="SourceSyncService"/>) and each source's new vacancies are delivered right after it
/// (<see cref="DeliveryService"/>), but the network requests for the sources that come next are already running
/// (<see cref="FetchPipeline"/>, up to <c>Polling:MaxParallelFetches</c> at once; HostThrottle keeps each host polite).
/// Once an owner has received MaxMessagesPerRun vacancies, none of their remaining sources are fetched in this cycle.
/// Safe to crash mid-way (state is saved per job).
///
/// Who may run or stop is <see cref="PollCoordinator"/>'s business; problems with one owner or source are
/// contained by <see cref="FailureAlerter"/>, so one owner's problem never aborts the cycle for the others.
/// </summary>
public sealed class PollRunner(
    IServiceScopeFactory scopes,
    FetcherRegistry fetchers,
    PollCoordinator coordinator,
    SourceSyncService sync,
    DeliveryService delivery,
    FailureAlerter alerter,
    IOptions<PollOptions> options,
    ILogger<PollRunner> log)
{
    private sealed record QueuedSource(Source Source, SearchProfile Profile, FetchContext? Plan);

    /// <summary>What is running, from the point of view of <paramref name="chatId"/>.</summary>
    public RunStatus StatusFor(long chatId) => coordinator.StatusFor(chatId);

    /// <summary>Cancels the manual check that <paramref name="chatId"/> started. Never touches anybody else's run.</summary>
    public StopOutcome RequestStop(long chatId) => coordinator.RequestStop(chatId);

    /// <param name="ownerChatId">Null = all users (scheduled run); otherwise only that user's profiles.</param>
    public async Task<PollResult> RunAsync(long? ownerChatId, CancellationToken ct)
    {
        using var lease = await coordinator.AcquireAsync(ownerChatId, ct);

        var sentBy = new Dictionary<long, int>();
        try
        {
            var runToken = lease.Token;
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
            var owners = sources.Select(s => profiles[s.SearchProfileId].OwnerChatId).Distinct().ToList();
            sentBy = owners.ToDictionary(o => o, _ => 0);
            var waitingBy = owners.ToDictionary(o => o, _ => 0);
            var skippedBy = owners.ToDictionary(o => o, _ => 0);
            var filteredBy = owners.ToDictionary(o => o, _ => 0);

            // Owners we stop serving for the rest of this cycle: chat unreachable, rate limited, or failing.
            var unavailable = new HashSet<long>();

            // Least recently fetched first: when the limit ends a cycle early, the sources that
            // didn't get a turn are the first ones to be fetched next time (no source starves).
            var queue = sources
                .OrderBy(s => s.LastSuccessUtc ?? DateTime.MinValue).ThenBy(s => s.Id)
                .Select(s =>
                {
                    var profile = profiles[s.SearchProfileId];
                    return new QueuedSource(s, profile, sync.Plan(s, profile));
                })
                .ToList();

            // Worth starting a request for: the owner can still receive and still has room.
            bool WantsFetch(QueuedSource q) =>
                !unavailable.Contains(q.Profile.OwnerChatId) && sentBy[q.Profile.OwnerChatId] < max;

            // Disposed at the end of this block, on a stop as well: cancels requests still in flight and waits for them.
            await using var fetches = new FetchPipeline(fetchers, options.Value.MaxParallelFetches, runToken);

            var fetched = 0;
            for (var i = 0; i < queue.Count; i++)
            {
                runToken.ThrowIfCancellationRequested();

                var (source, profile, plan) = queue[i];
                var owner = profile.OwnerChatId;
                if (unavailable.Contains(owner))
                {
                    fetches.Forget(plan);
                    continue;
                }
                if (sentBy[owner] >= max)
                {
                    fetches.Forget(plan);
                    skippedBy[owner]++;
                    continue;
                }

                // This source first, then the ones behind it, so their requests run while this one is processed.
                fetches.Prefetch(queue.Skip(i).Where(q => q.Plan is not null && WantsFetch(q)).Select(q => q.Plan!));

                await sync.SyncAsync(db, source, profile, fetches, unavailable, runToken);
                fetched++;

                // The failure alert may have discovered that this chat is unreachable.
                if (unavailable.Contains(owner)) continue;

                // Send what THIS source just found, right now (newest first). Older leftovers don't jump the queue.
                // (A digest user gets nothing here; their digest is built in the final pass below.)
                var d = await delivery.DeliverSafelyAsync(db, owner, max - sentBy[owner], source.Id, unavailable, runToken);
                sentBy[owner] += d.Sent;
                filteredBy[owner] += d.Filtered;

                // Everybody is full (or out of reach): stop fetching.
                if (owners.All(o => unavailable.Contains(o) || sentBy[o] >= max)) break;
            }

            // Leftovers from earlier runs go out only if there is room, newest first. With no room this
            // just counts what is waiting. This is also where a due digest is sent.
            foreach (var owner in owners)
            {
                if (unavailable.Contains(owner)) continue;

                var d = await delivery.DeliverSafelyAsync(db, owner, max - sentBy[owner], null, unavailable, runToken);
                sentBy[owner] += d.Sent;

                // A digest has its own size limit and says itself what follows in the next one; the per-run
                // limit notice below is about instant messages.
                waitingBy[owner] = d.Digest ? 0 : d.Waiting;
                filteredBy[owner] += d.Filtered;
            }

            foreach (var owner in owners)
            {
                if (unavailable.Contains(owner)) continue;

                var skipped = skippedBy[owner];
                if (sentBy[owner] >= max && (waitingBy[owner] > 0 || skipped > 0))
                    await alerter.NotifyAsync(db, owner,
                        MessageFormatter.LimitReached(max, waitingBy[owner], skipped), unavailable, runToken);
                else if (ownerChatId is not null && sentBy[owner] == 0 && filteredBy[owner] > 0)
                    // Manual /check found keyword matches but the profile's filters removed all of them.
                    await alerter.NotifyAsync(db, owner,
                        MessageFormatter.DroppedByFilters(filteredBy[owner]), unavailable, runToken);
            }

            var sent = sentBy.Values.Sum();
            log.LogInformation(
                "Poll finished: {Fetched}/{Sources} sources checked, {Sent} vacancies sent (limit {Max} per user), {Skipped} owner(s) skipped",
                fetched, sources.Count, sent, max, unavailable.Count);
            return new PollResult(sent, false);
        }
        catch (OperationCanceledException) when (lease.StopRequested && !ct.IsCancellationRequested)
        {
            // /stop (or the Stop button): not an error. Everything delivered so far is already saved.
            var sent = sentBy.Values.Sum();
            log.LogInformation("Poll stopped by user: {Sent} vacancies sent before the stop", sent);
            return new PollResult(sent, true);
        }
    }
}