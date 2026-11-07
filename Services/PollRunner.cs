using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

/// <summary>Outcome of one poll cycle.</summary>
public readonly record struct PollResult(int Sent, bool Stopped);

/// <summary>What is running right now, seen from one user's point of view.</summary>
public enum RunStatus
{
    Idle,
    /// <summary>The hourly all-users cycle. It covers everybody and can't be stopped from chat.</summary>
    Scheduled,
    /// <summary>A manual check started by this user.</summary>
    Yours,
    /// <summary>A manual check started by someone else. This user's own check would wait its turn.</summary>
    Other
}

public enum StopOutcome
{
    Stopped,
    /// <summary>Only the scheduled run is active; users can't stop it.</summary>
    ScheduledRun,
    /// <summary>Nothing is running, or the active run belongs to someone else.</summary>
    NothingOfYours
}

/// <summary>
/// One poll cycle. Sources are fetched ONE AT A TIME and each source's new vacancies are filtered and sent
/// right after it is fetched. Once an owner has received MaxMessagesPerRun vacancies, none of their remaining
/// sources are fetched in this cycle. Runs are serialized (one at a time) and safe to crash mid-way
/// (state is saved per job).
///
/// One owner's problem never aborts the cycle for the others: a chat that blocked the bot is disabled,
/// a rate-limited or failing chat is skipped until the next cycle.
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
    private readonly object _stateLock = new();
    private ActiveRun? _active;

    private sealed class ActiveRun(long? owner, CancellationTokenSource cts)
    {
        public long? Owner { get; } = owner;
        public CancellationTokenSource Cts { get; } = cts;
        public volatile bool StopRequested;
    }

    /// <summary>What is running, from the point of view of <paramref name="chatId"/>.</summary>
    public RunStatus StatusFor(long chatId)
    {
        lock (_stateLock)
        {
            if (_active is null) return RunStatus.Idle;
            if (_active.Owner is null) return RunStatus.Scheduled;
            return _active.Owner == chatId ? RunStatus.Yours : RunStatus.Other;
        }
    }

    /// <summary>Cancels the manual check that <paramref name="chatId"/> started. Never touches anybody else's run.</summary>
    public StopOutcome RequestStop(long chatId)
    {
        lock (_stateLock)
        {
            if (_active is null) return StopOutcome.NothingOfYours;
            if (_active.Owner is null) return StopOutcome.ScheduledRun;
            if (_active.Owner != chatId) return StopOutcome.NothingOfYours;

            _active.StopRequested = true;
            _active.Cts.Cancel();
            return StopOutcome.Stopped;
        }
    }

    /// <summary>Result of one delivery pass.</summary>
    private readonly record struct Delivery(int Sent, int Waiting, int Filtered, int Duplicates);

    /// <param name="ownerChatId">Null = all users (scheduled run); otherwise only that user's profiles.</param>
    public async Task<PollResult> RunAsync(long? ownerChatId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new ActiveRun(ownerChatId, runCts);
        lock (_stateLock) { _active = run; }

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
            var owners = sources.Select(s => profiles[s.SearchProfileId].OwnerChatId).Distinct().ToList();
            sentBy = owners.ToDictionary(o => o, _ => 0);
            var waitingBy = owners.ToDictionary(o => o, _ => 0);
            var skippedBy = owners.ToDictionary(o => o, _ => 0);
            var filteredBy = owners.ToDictionary(o => o, _ => 0);

            // Owners we stop serving for the rest of this cycle: chat unreachable, rate limited, or failing.
            var unavailable = new HashSet<long>();

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

                var owner = profiles[source.SearchProfileId].OwnerChatId;
                if (unavailable.Contains(owner)) continue;
                if (sentBy[owner] >= max)
                {
                    skippedBy[owner]++;
                    continue;
                }

                await FetchSourceAsync(db, source, profiles[source.SearchProfileId], cache, unavailable, runToken);
                fetched++;

                // The failure alert may have discovered that this chat is unreachable.
                if (unavailable.Contains(owner)) continue;

                // Send what THIS source just found, right now (newest first). Older leftovers don't jump the queue.
                var d = await DeliverSafelyAsync(db, owner, max - sentBy[owner], source.Id, unavailable, runToken);
                sentBy[owner] += d.Sent;
                filteredBy[owner] += d.Filtered;

                // Everybody is full (or out of reach): stop fetching.
                if (owners.All(o => unavailable.Contains(o) || sentBy[o] >= max)) break;
            }

            // Leftovers from earlier runs go out only if there is room, newest first. With no room this
            // just counts what is waiting.
            foreach (var owner in owners)
            {
                if (unavailable.Contains(owner)) continue;

                var d = await DeliverSafelyAsync(db, owner, max - sentBy[owner], null, unavailable, runToken);
                sentBy[owner] += d.Sent;
                waitingBy[owner] = d.Waiting;
                filteredBy[owner] += d.Filtered;
            }

            foreach (var owner in owners)
            {
                if (unavailable.Contains(owner)) continue;

                var skipped = skippedBy[owner];
                if (sentBy[owner] >= max && (waitingBy[owner] > 0 || skipped > 0))
                    await NotifyAsync(db, owner,
                        $"… limit of {max} reached for this run" +
                        (waitingBy[owner] > 0 ? $", {waitingBy[owner]} more matching vacancies are waiting" : "") +
                        (skipped > 0 ? $", {skipped} sources were not checked yet" : "") +
                        ". They will arrive on the next run.", unavailable, runToken);
                else if (ownerChatId is not null && sentBy[owner] == 0 && filteredBy[owner] > 0)
                    // Manual /check found keyword matches but the profile's filters removed all of them.
                    await NotifyAsync(db, owner,
                        $"ℹ️ {filteredBy[owner]} vacancies matched your keyword but were dropped by your profile filters " +
                        "(Include, Exclude, Companies, Locations, Min salary, Remote only). Open 🎯 Filters to check them.",
                        unavailable, runToken);
            }

            var sent = sentBy.Values.Sum();
            log.LogInformation(
                "Poll finished: {Fetched}/{Sources} sources checked, {Sent} vacancies sent (limit {Max} per user), {Skipped} owner(s) skipped",
                fetched, sources.Count, sent, max, unavailable.Count);
            return new PollResult(sent, false);
        }
        catch (OperationCanceledException) when (run.StopRequested && !ct.IsCancellationRequested)
        {
            // /stop (or the Stop button): not an error. Everything delivered so far is already saved.
            var sent = sentBy.Values.Sum();
            log.LogInformation("Poll stopped by user: {Sent} vacancies sent before the stop", sent);
            return new PollResult(sent, true);
        }
        finally
        {
            lock (_stateLock) { _active = null; }
            _gate.Release();
        }
    }

    private async Task FetchSourceAsync(
        AppDb db, Source source, SearchProfile profile, Dictionary<string, IReadOnlyList<FetchedJob>> cache,
        HashSet<long> unavailable, CancellationToken ct)
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

            // Scope for vacancies without a company, see JobHasher.
            var hashScope = source.Provider ?? $"source{source.Id}";

            var now = DateTime.UtcNow;
            var added = 0;

            foreach (var item in items)
            {
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

            log.LogInformation("Source {Id} '{Name}' (profile '{Profile}'): {Total} items match the keyword, {Added} new", source.Id, source.Name, profile.Name, items.Count, added);
        }
        // "!ct.IsCancellationRequested" instead of "is not OperationCanceledException": an HttpClient timeout
        // is reported as a TaskCanceledException even though nobody cancelled us. That is an ordinary source
        // failure, not a stop request, and must not abort the whole cycle.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Source {Id} '{Name}' (profile '{Profile}') failed: {Error}", source.Id, source.Name, profile.Name, ex.Message);

            source.ConsecutiveFailures++;
            source.LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            await db.SaveChangesAsync(ct);

            // Usually means the site changed or blocked us. Tell the owner once, not every hour.
            if (source.ConsecutiveFailures == FailureAlertThreshold)
            {
                await NotifyAsync(db, profile.OwnerChatId,
                    $"⚠️ <b>{Html(source.Name)}</b> in profile <b>{Html(profile.Name)}</b> failed {FailureAlertThreshold} times in a row:\n<code>{Html(source.LastError)}</code>",
                    unavailable, ct);
            }
        }
    }

    /// <summary>
    /// <see cref="DeliverAsync"/> with the owner's problems contained: whatever goes wrong while talking to
    /// this one chat, the cycle goes on with the other owners.
    /// </summary>
    private async Task<Delivery> DeliverSafelyAsync(
        AppDb db, long owner, int budget, int? onlySourceId, HashSet<long> unavailable, CancellationToken ct)
    {
        try
        {
            return await DeliverAsync(db, owner, budget, onlySourceId, ct);
        }
        catch (RecipientUnreachableException ex)
        {
            await DisableOwnerAsync(db, owner, ex.Message, unavailable, ct);
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
    /// The chat can't receive messages (the user blocked the bot or deleted the chat). Disable its profiles so
    /// we stop fetching and trying every hour. The user can re-enable them from the menu if they come back.
    /// </summary>
    private async Task DisableOwnerAsync(AppDb db, long owner, string reason, HashSet<long> unavailable, CancellationToken ct)
    {
        unavailable.Add(owner);

        var disabled = await db.SearchProfiles
            .Where(p => p.OwnerChatId == owner && p.Enabled)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false), ct);

        log.LogWarning("Chat {Owner} can't be reached ({Reason}); disabled {Count} profile(s)", owner, reason, disabled);
    }

    /// <summary>
    /// Filters the owner's pending vacancies (only those of <paramref name="onlySourceId"/>, or all of them when null)
    /// and sends at most <paramref name="budget"/> of them, newest first. With budget 0 nothing is sent: matching
    /// vacancies are just counted as waiting.
    /// </summary>
    private async Task<Delivery> DeliverAsync(AppDb db, long owner, int budget, int? onlySourceId, CancellationToken ct)
    {
        if (budget < 0) budget = 0;

        var settings = await db.UserSettings.FindAsync(new object[] { owner }, ct) ?? new UserSettings { ChatId = owner };
        if (settings.Paused) return new Delivery(0, 0, 0, 0);

        // Digest mode: only deliver during the configured hour (UTC).
        if (settings.Delivery == DeliveryMode.Digest && DateTime.UtcNow.Hour != settings.DigestHourUtc)
            return new Delivery(0, 0, 0, 0);

        var sources = await db.Sources
            .Where(s => s.SearchProfile.OwnerChatId == owner)
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

            try
            {
                await notifier.SendAsync(owner, FormatMessage(job, source.Name, profile), ct);
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

        return new Delivery(sent, waiting, filteredOut, duplicates);
    }

    private const int SnippetLength = 350;
    private const int MaxLineLength = 200;

    private static string FormatMessage(Job job, string sourceName, SearchProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(job.Title)}</b>");

        // Who / where
        if (!string.IsNullOrWhiteSpace(job.Company)) sb.AppendLine($"🏢 {Html(job.Company)}");

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Location)) where.Add($"📍 {Html(job.Location)}");
        if (FilterEngine.IsRemote(job)) where.Add("🌐 Remote");
        if (where.Count > 0) sb.AppendLine(string.Join(" · ", where));

        // Terms
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.EmploymentType)) terms.Add($"💼 {Html(job.EmploymentType)}");
        if (job.Salary is { } salary) terms.Add($"💰 {salary:N0}");
        if (terms.Count > 0) sb.AppendLine(string.Join(" · ", terms));

        // Freshness
        sb.AppendLine(job.PostedUtc is { } posted
            ? $"🕒 Posted {Ago(posted)} ({posted:yyyy-MM-dd})"
            : $"🕒 First seen {Ago(job.FirstSeenUtc)}");

        sb.AppendLine($"🔎 <i>{Html(profile.Name)} · {Html(sourceName)}</i>");

        // Why it matched
        var criteria = FilterEngine.Explain(job, profile);
        if (criteria.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("✅ <b>Matched</b>");
            foreach (var line in criteria)
                sb.AppendLine($"• {Html(Truncate(line, MaxLineLength))}");
        }

        // Description preview
        var snippet = Snippet(job.Description);
        if (snippet.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"📝 {Html(snippet)}");
        }

        sb.AppendLine();
        sb.Append($"<a href=\"{WebUtility.HtmlEncode(job.Url)}\">Open vacancy</a>");
        return sb.ToString();
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)span.TotalMinutes)} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        return $"{(int)span.TotalDays} days ago";
    }

    private static string Snippet(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var text = WebUtility.HtmlDecode(HtmlTags().Replace(description, " "));
        text = Whitespace().Replace(text, " ").Trim();
        return Truncate(text, SnippetLength);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>Best-effort message (alerts, limit notices). Never throws; an unreachable chat gets its profiles disabled.</summary>
    private async Task NotifyAsync(AppDb db, long chatId, string html, HashSet<long> unavailable, CancellationToken ct)
    {
        try
        {
            await notifier.SendAsync(chatId, html, ct);
        }
        catch (RecipientUnreachableException ex)
        {
            await DisableOwnerAsync(db, chatId, ex.Message, unavailable, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Failed to send message to {ChatId}", chatId);
        }
    }

    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

    [GeneratedRegex(@"<[^>]+>")] private static partial Regex HtmlTags();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
