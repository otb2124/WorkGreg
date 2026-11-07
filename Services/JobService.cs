using WorkGreg.Data;
using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Services;

/// <summary>Filters and paging for <see cref="JobService.ListAsync"/>. Skip/Take are clamped by the service.</summary>
public sealed record JobQuery(
    int? ProfileId = null,
    int? SourceId = null,
    bool? Delivered = null,
    string? Search = null,
    int Skip = 0,
    int Take = 50);

public sealed record JobListItem(Job Job, int ProfileId, string ProfileName, string SourceName);

public sealed record JobPage(int Total, IReadOnlyList<JobListItem> Items);

/// <summary>Dashboard counts for one user.</summary>
public sealed record UserSummary(
    int Profiles, int EnabledProfiles, int Sources, int EnabledSources, int FailingSources,
    int PendingJobs, int DeliveredJobs, bool Paused);

/// <summary>
/// Use cases around stored vacancies and manual checks. Used by the Telegram bot and the HTTP API.
/// </summary>
public sealed class JobService(AppDb db, PollRunner runner)
{
    public const int MaxPageSize = 200;

    public async Task<JobPage> ListAsync(long chatId, JobQuery q, CancellationToken ct)
    {
        var skip = Math.Max(q.Skip, 0);
        var take = Math.Clamp(q.Take, 1, MaxPageSize);

        var query = db.Jobs.AsNoTracking().Where(j => j.OwnerChatId == chatId);

        if (q.ProfileId is { } profileId)
        {
            var ids = await db.Sources
                .Where(s => s.OwnerChatId == chatId && s.SearchProfileId == profileId)
                .Select(s => s.Id)
                .ToListAsync(ct);
            query = query.Where(j => ids.Contains(j.SourceId));
        }

        if (q.SourceId is { } sourceId) query = query.Where(j => j.SourceId == sourceId);
        if (q.Delivered is { } delivered) query = query.Where(j => j.Delivered == delivered);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(j => j.Title.Contains(term) || (j.Company != null && j.Company.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var jobs = await query
            .OrderByDescending(j => j.PostedUtc ?? j.FirstSeenUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.OwnerChatId == chatId)
            .ToDictionaryAsync(s => s.Id, ct);
        var profileNames = await db.SearchProfiles.AsNoTracking()
            .Where(p => p.OwnerChatId == chatId)
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var items = jobs.Select(j =>
        {
            sources.TryGetValue(j.SourceId, out var source);
            var profileOfJob = source?.SearchProfileId ?? 0;
            return new JobListItem(j, profileOfJob, profileNames.GetValueOrDefault(profileOfJob, "?"), source?.Name ?? "?");
        }).ToList();

        return new JobPage(total, items);
    }

    public async Task<UserSummary> GetSummaryAsync(long chatId, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.AsNoTracking().Where(p => p.OwnerChatId == chatId).ToListAsync(ct);
        var sources = await db.Sources.AsNoTracking().Where(s => s.OwnerChatId == chatId).ToListAsync(ct);
        var pending = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId && !j.Processed, ct);
        var delivered = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId && j.Delivered, ct);
        var paused = await db.Profiles.AsNoTracking()
            .Where(s => s.ChatId == chatId)
            .Select(s => s.Paused)
            .FirstOrDefaultAsync(ct);

        return new UserSummary(
            profiles.Count,
            profiles.Count(p => p.Enabled),
            sources.Count,
            sources.Count(s => s.Enabled),
            sources.Count(s => s.ConsecutiveFailures > 0),
            pending,
            delivered,
            paused);
    }

    public Task<int> CountStoredAsync(long chatId, CancellationToken ct) =>
        db.Jobs.CountAsync(j => j.OwnerChatId == chatId, ct);

    /// <summary>Marks every stored vacancy as not yet sent. Returns how many were reset.</summary>
    public async Task<Result<int, Error>> ResendAllAsync(long chatId, CancellationToken ct)
    {
        // Delivered must be cleared too: delivery treats "same Hash already Delivered"
        // as a duplicate, so every job would skip itself.
        return await db.Jobs
            .Where(j => j.OwnerChatId == chatId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Processed, false)
                .SetProperty(j => j.Delivered, false), ct);
    }

    /// <summary>
    /// Runs a poll cycle for this user. Busy when this user's own check, or the scheduled hourly check, is running.
    /// (Somebody else's manual check is not an error: this one simply waits its turn.)
    /// </summary>
    public async Task<Result<PollResult, Error>> CheckNowAsync(long chatId, CancellationToken ct)
    {
        switch (runner.StatusFor(chatId))
        {
            case RunStatus.Yours:
                return Error.Busy("A check is already running.");
            case RunStatus.Scheduled:
                return Error.Busy("The hourly check is running right now.");
        }

        return await runner.RunAsync(chatId, ct);
    }
}