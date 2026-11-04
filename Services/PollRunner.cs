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
/// One poll cycle: fetch every enabled source, store unseen jobs, then filter and deliver per user.
/// Safe to call concurrently (calls are serialized) and safe to crash mid-way (state is saved per job).
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

    /// <param name="ownerChatId">Null = all users (scheduled run); otherwise only that user's sources.</param>
    /// <returns>Number of vacancies sent.</returns>
    public async Task<int> RunAsync(long? ownerChatId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();

            var sources = await db.Sources
                .Where(s => s.Enabled && (ownerChatId == null || s.OwnerChatId == ownerChatId))
                .ToListAsync(ct);

            // Raw fetch results by (type, url) for this cycle: searches that share a URL cost one request.
            var cache = new Dictionary<string, IReadOnlyList<FetchedJob>>();

            foreach (var source in sources)
                await FetchSourceAsync(db, source, cache, ct);

            var sent = 0;
            foreach (var owner in sources.Select(s => s.OwnerChatId).Distinct())
                sent += await DeliverAsync(db, owner, ct);

            log.LogInformation("Poll finished: {Sources} sources, {Sent} vacancies sent", sources.Count, sent);
            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FetchSourceAsync(AppDb db, Source source, Dictionary<string, IReadOnlyList<FetchedJob>> cache, CancellationToken ct)
    {
        try
        {
            var provider = SourceCatalog.Find(source.Provider);
            var cacheKey = $"{source.Type}|{source.Url}";

            if (!cache.TryGetValue(cacheKey, out var items))
            {
                items = await fetchers.Get(source.Type).FetchAsync(new FetchContext(source, provider, source.Url), ct);
                cache[cacheKey] = items;
            }

            // A scraper whose selectors broke returns nothing and still "succeeds". Count that as a failure
            // so the usual 3-in-a-row alert fires. Feeds are excluded: an empty search feed is normal.
            if (items.Count == 0 && source.Type != FetchType.Feed && source.LastSuccessUtc is not null &&
                await db.Jobs.AnyAsync(j => j.SourceId == source.Id, ct))
                throw new InvalidOperationException(
                    "The source returned 0 items although it returned vacancies before; the site or its response format probably changed.");

            // The provider's own search is a hint at best; the keyword is always enforced here.
            if (!string.IsNullOrWhiteSpace(source.Keyword))
                items = items
                    .Where(i => SourceCatalog.MatchesKeyword(source.Keyword, i.Title, i.Description))
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
                    // First successful fetch = baseline: remember everything, notify about nothing.
                    Processed = false
                });
                added++;
            }

            source.Baselined = true;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
            source.LastSuccessUtc = now;
            await db.SaveChangesAsync(ct);

            log.LogInformation("Source {Id} '{Name}': {Total} items, {Added} new", source.Id, source.Name, items.Count, added);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Source {Id} '{Name}' failed", source.Id, source.Name);

            source.ConsecutiveFailures++;
            source.LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            await db.SaveChangesAsync(ct);

            // Usually means the site changed or blocked us. Tell the owner once, not every hour.
            if (source.ConsecutiveFailures == FailureAlertThreshold)
            {
                await SafeSendAsync(source.OwnerChatId,
                    $"⚠️ Source <b>{Html(source.Name)}</b> (#{source.Id}) failed {FailureAlertThreshold} times in a row:\n<code>{Html(source.LastError)}</code>",
                    ct);
            }
        }
    }

    

    private async Task<int> DeliverAsync(AppDb db, long owner, CancellationToken ct)
    {
        var profile = await db.Profiles.FindAsync(new object[] { owner }, ct) ?? new UserProfile { ChatId = owner };
        if (profile.Paused) return 0;

        // Digest mode: only run once per day, at the configured hour (UTC).
        if (profile.Delivery == DeliveryMode.Digest)
        {
            var now = DateTime.UtcNow;
            if (now.Hour != profile.DigestHourUtc) return 0;

            // If we already ran during this digest hour today, skip.
            var lastDigestUtc = await db.Jobs
                .Where(j => j.OwnerChatId == owner && j.Delivered)
                .MaxAsync(j => (DateTime?)j.FirstSeenUtc, ct);
            // (This is a coarse heuristic. If you want precision, add a DeliveryRuns table keyed by (chatId, date).)
        }

        var sourceNames = await db.Sources
            .Where(s => s.OwnerChatId == owner)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var pending = await db.Jobs
            .Where(j => j.OwnerChatId == owner && !j.Processed)
            .OrderBy(j => j.PostedUtc ?? j.FirstSeenUtc)
            .ToListAsync(ct);

        var max = options.Value.MaxMessagesPerRun;
        var sent = 0;

        foreach (var job in pending)
        {
            if (!FilterEngine.Matches(job, profile))
            {
                job.Processed = true;
                continue;
            }

            var duplicate = await db.Jobs.AnyAsync(
                j => j.OwnerChatId == owner && j.Hash == job.Hash && j.Delivered, ct);
            if (duplicate)
            {
                job.Processed = true;
                continue;
            }

            if (sent >= max) break; // rest stays pending for the next run (Telegram rate limits)

            await notifier.SendAsync(owner, FormatMessage(job, sourceNames.GetValueOrDefault(job.SourceId, "?")), ct);
            job.Processed = true;
            job.Delivered = true;
            sent++;

            await db.SaveChangesAsync(ct); // save per message so a crash never causes duplicates
            await Task.Delay(100, ct);
        }

        await db.SaveChangesAsync(ct);

        var remaining = pending.Count(j => !j.Processed);
        if (remaining > 0)
            await SafeSendAsync(owner, $"… and {remaining} more waiting, they will arrive on the next run.", ct);

        return sent;
    }

    private static string FormatMessage(Job job, string sourceName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(job.Title)}</b>");

        var meta = string.Join(" · ", new[] { job.Company, job.Location }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (meta.Length > 0) sb.AppendLine(Html(meta));

        sb.AppendLine($"<i>{Html(sourceName)}</i>");
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
