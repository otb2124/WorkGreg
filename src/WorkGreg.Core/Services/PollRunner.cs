using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;

namespace WorkGreg.Services;


/// <summary>
/// One poll cycle: decides the ORDER of work and keeps the per-owner counters. Sources are handled ONE AT A TIME
/// (<see cref="SourceSyncService"/>) and each source's new vacancies are delivered right after it
/// (<see cref="DeliveryService"/>), but the network requests for the sources that come next are already running
/// (<see cref="FetchPipeline"/>, up to <c>Polling:MaxParallelFetches</c> at once; HostThrottle keeps each host polite).
/// Once an owner has received MaxMessagesPerRun vacancies, none of their remaining sources are fetched in this cycle.
/// Safe to crash mid-way (state is saved per job).
///
/// The cycle is four steps, one method each: <see cref="LoadQueueAsync"/>, <see cref="ProcessSourcesAsync"/>,
/// <see cref="FinalDeliveryAsync"/> and <see cref="SendSummariesAsync"/>. They share one <see cref="Cycle"/>.
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

    /// <summary>What happened to one owner in this cycle. One of these per owner replaces the four parallel dictionaries.</summary>
    private sealed record OwnerTally
    {
        /// <summary>Vacancies sent (a digest counts every vacancy in it).</summary>
        public int Sent { get; set; }

        /// <summary>Matching vacancies left for a later cycle because the per-run limit was reached.</summary>
        public int Waiting { get; set; }

        /// <summary>Sources not fetched because the owner was already at the limit.</summary>
        public int Skipped { get; set; }

        /// <summary>Vacancies the profile's filters removed.</summary>
        public int Filtered { get; set; }
    }

    /// <summary>Everything the steps of one cycle share.</summary>
    private sealed class Cycle(AppDb db, int max, IReadOnlyDictionary<long, OwnerTally> tallies, CancellationToken token)
    {
        public AppDb Db { get; } = db;
        public int Max { get; } = max;
        public CancellationToken Token { get; } = token;
        public IReadOnlyDictionary<long, OwnerTally> Tallies { get; } = tallies;

        /// <summary>Owners we stop serving for the rest of this cycle: chat unreachable, rate limited, or failing.</summary>
        public HashSet<long> Unavailable { get; } = [];

        public bool AtLimit(long owner) => Tallies[owner].Sent >= Max;

        /// <summary>Owners who can still receive and still have room.</summary>
        public bool CanReceive(long owner) => !Unavailable.Contains(owner) && !AtLimit(owner);

        /// <summary>Everybody is full (or out of reach): nothing more to fetch.</summary>
        public bool Finished => Tallies.Keys.All(o => !CanReceive(o));
    }

    /// <summary>What is running, from the point of view of <paramref name="chatId"/>.</summary>
    public RunStatus StatusFor(long chatId) => coordinator.StatusFor(chatId);

    /// <summary>Cancels the manual check that <paramref name="chatId"/> started. Never touches anybody else's run.</summary>
    public StopOutcome RequestStop(long chatId) => coordinator.RequestStop(chatId);

    /// <param name="ownerChatId">Null = all users (scheduled run); otherwise only that user's profiles.</param>
    public async Task<PollResult> RunAsync(long? ownerChatId, CancellationToken ct)
    {
        using var lease = await coordinator.AcquireAsync(ownerChatId, ct);

        var tallies = new Dictionary<long, OwnerTally>();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();

            var queue = await LoadQueueAsync(db, ownerChatId, lease.Token);
            foreach (var q in queue) tallies.TryAdd(q.Profile.OwnerChatId, new OwnerTally());

            var cycle = new Cycle(db, options.Value.MaxMessagesPerRun, tallies, lease.Token);

            var fetched = await ProcessSourcesAsync(cycle, queue);
            await FinalDeliveryAsync(cycle);
            await SendSummariesAsync(cycle, manual: ownerChatId is not null);

            var sent = tallies.Values.Sum(t => t.Sent);
            log.LogInformation(
                "Poll finished: {Fetched}/{Sources} sources checked, {Sent} vacancies sent (limit {Max} per user), {Skipped} owner(s) skipped",
                fetched, queue.Count, sent, cycle.Max, cycle.Unavailable.Count);
            return new PollResult(sent, false);
        }
        catch (OperationCanceledException) when (lease.StopRequested && !ct.IsCancellationRequested)
        {
            // /stop (or the Stop button): not an error. Everything delivered so far is already saved.
            var sent = tallies.Values.Sum(t => t.Sent);
            log.LogInformation("Poll stopped by user: {Sent} vacancies sent before the stop", sent);
            return new PollResult(sent, true);
        }
    }

    // ---- 1. what to fetch, in which order

    /// <summary>
    /// The enabled sources of the enabled profiles in scope, least recently fetched first: when the limit ends a
    /// cycle early, the sources that didn't get a turn are the first ones to be fetched next time (no source starves).
    /// </summary>
    private async Task<List<QueuedSource>> LoadQueueAsync(AppDb db, long? ownerChatId, CancellationToken token)
    {
        var profiles = (await db.SearchProfiles
                .Where(p => p.Enabled && (ownerChatId == null || p.OwnerChatId == ownerChatId))
                .ToListAsync(token))
            .ToDictionary(p => p.Id);

        var profileIds = profiles.Keys.ToList();
        var sources = await db.Sources
            .Where(s => s.Enabled && profileIds.Contains(s.SearchProfileId))
            .ToListAsync(token);

        return sources
            .OrderBy(s => s.LastSuccessUtc ?? DateTime.MinValue).ThenBy(s => s.Id)
            .Select(s =>
            {
                var profile = profiles[s.SearchProfileId];
                return new QueuedSource(s, profile, sync.Plan(s, profile));
            })
            .ToList();
    }

    // ---- 2. one source at a time, each followed by its own delivery

    /// <summary>Returns how many sources were fetched.</summary>
    private async Task<int> ProcessSourcesAsync(Cycle cycle, List<QueuedSource> queue)
    {
        var token = cycle.Token;

        // Disposed when the loop ends, on a stop as well: cancels requests still in flight and waits for them.
        await using var fetches = new FetchPipeline(fetchers, options.Value.MaxParallelFetches, token);

        var fetched = 0;
        for (var i = 0; i < queue.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var (source, profile, plan) = queue[i];
            var owner = profile.OwnerChatId;
            if (cycle.Unavailable.Contains(owner))
            {
                fetches.Forget(plan);
                continue;
            }
            if (cycle.AtLimit(owner))
            {
                fetches.Forget(plan);
                cycle.Tallies[owner].Skipped++;
                continue;
            }

            // This source first, then the ones behind it, so their requests run while this one is processed.
            fetches.Prefetch(queue.Skip(i)
                .Where(q => q.Plan is not null && cycle.CanReceive(q.Profile.OwnerChatId))
                .Select(q => q.Plan!));

            await sync.SyncAsync(cycle.Db, source, profile, fetches, cycle.Unavailable, token);
            fetched++;

            // The failure alert may have discovered that this chat is unreachable.
            if (cycle.Unavailable.Contains(owner)) continue;

            // Send what THIS source just found, right now (newest first). Older leftovers don't jump the queue.
            // (A digest user gets nothing here; their digest is built in the final pass.)
            var tally = cycle.Tallies[owner];
            var d = await delivery.DeliverSafelyAsync(
                cycle.Db, owner, cycle.Max - tally.Sent, source.Id, cycle.Unavailable, token);
            tally.Sent += d.Sent;
            tally.Filtered += d.Filtered;

            if (cycle.Finished) break;
        }

        return fetched;
    }

    // ---- 3. leftovers and digests

    /// <summary>
    /// Leftovers from earlier runs go out only if there is room, newest first. With no room this just counts what
    /// is waiting. This is also where a due digest is sent.
    /// </summary>
    private async Task FinalDeliveryAsync(Cycle cycle)
    {
        foreach (var (owner, tally) in cycle.Tallies)
        {
            if (cycle.Unavailable.Contains(owner)) continue;

            var d = await delivery.DeliverSafelyAsync(
                cycle.Db, owner, cycle.Max - tally.Sent, null, cycle.Unavailable, cycle.Token);
            tally.Sent += d.Sent;

            // A digest has its own size limit and says itself what follows in the next one; the per-run
            // limit notice is about instant messages.
            tally.Waiting = d.Digest ? 0 : d.Waiting;
            tally.Filtered += d.Filtered;
        }
    }

    // ---- 4. the notices at the end

    /// <param name="manual">A manual /check: also say when the filters removed everything that matched.</param>
    private async Task SendSummariesAsync(Cycle cycle, bool manual)
    {
        foreach (var (owner, tally) in cycle.Tallies)
        {
            if (cycle.Unavailable.Contains(owner)) continue;

            if (tally.Sent >= cycle.Max && (tally.Waiting > 0 || tally.Skipped > 0))
                await alerter.NotifyAsync(cycle.Db, owner,
                    MessageFormatter.LimitReached(cycle.Max, tally.Waiting, tally.Skipped), cycle.Unavailable, cycle.Token);
            else if (manual && tally.Sent == 0 && tally.Filtered > 0)
                // Manual /check found keyword matches but the profile's filters removed all of them.
                await alerter.NotifyAsync(cycle.Db, owner,
                    MessageFormatter.DroppedByFilters(tally.Filtered), cycle.Unavailable, cycle.Token);
        }
    }
}
