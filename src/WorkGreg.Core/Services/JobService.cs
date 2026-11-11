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

/// <summary>
/// One of a user's vacancies as the API and the UI see it: the shared <see cref="Job"/> data plus this user's
/// <see cref="JobDelivery"/> state. <paramref name="Id"/> is the delivery's id, so it is unique per list row even when
/// two of the user's profiles found the same vacancy.
/// </summary>
public sealed record JobView(
    int Id, int SourceId, string Title, string? Company, string? Location, string Url, string? Description,
    DateTime? PostedUtc, DateTime FirstSeenUtc, bool Processed, bool Delivered);

public sealed record JobListItem(JobView Job, int ProfileId, string ProfileName, string SourceName);

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

        var query = db.JobDeliveries.AsNoTracking().Where(d => d.OwnerChatId == chatId);

        if (q.ProfileId is { } profileId)
        {
            var ids = await db.Sources
                .Where(s => s.SearchProfile.OwnerChatId == chatId && s.SearchProfileId == profileId)
                .Select(s => s.Id)
                .ToListAsync(ct);
            query = query.Where(d => ids.Contains(d.SourceId));
        }

        if (q.SourceId is { } sourceId) query = query.Where(d => d.SourceId == sourceId);
        if (q.Delivered is { } delivered) query = query.Where(d => d.Delivered == delivered);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(d => d.Job.Title.Contains(term) || (d.Job.Company != null && d.Job.Company.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .Include(d => d.Job)
            .OrderByDescending(d => d.Job.PostedUtc ?? d.CreatedUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.SearchProfile.OwnerChatId == chatId)
            .ToDictionaryAsync(s => s.Id, ct);
        var profileNames = await db.SearchProfiles.AsNoTracking()
            .Where(p => p.OwnerChatId == chatId)
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var items = rows.Select(d =>
        {
            sources.TryGetValue(d.SourceId, out var source);
            var profileOfJob = source?.SearchProfileId ?? 0;
            var job = d.Job;

            var view = new JobView(
                d.Id, d.SourceId, job.Title, job.Company, job.Location, job.Url, job.Description,
                job.PostedUtc, d.CreatedUtc, d.Processed, d.Delivered);

            return new JobListItem(view, profileOfJob, profileNames.GetValueOrDefault(profileOfJob, "?"), source?.Name ?? "?");
        }).ToList();

        return new JobPage(total, items);
    }

    public async Task<UserSummary> GetSummaryAsync(long chatId, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.AsNoTracking().Where(p => p.OwnerChatId == chatId).ToListAsync(ct);
        var sources = await db.Sources.AsNoTracking().Where(s => s.SearchProfile.OwnerChatId == chatId).ToListAsync(ct);
        var pending = await db.JobDeliveries.CountAsync(d => d.OwnerChatId == chatId && !d.Processed, ct);
        var delivered = await db.JobDeliveries.CountAsync(d => d.OwnerChatId == chatId && d.Delivered, ct);
        var paused = await db.UserSettings.AsNoTracking()
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

    /// <summary>How many vacancies are stored for this user (their deliveries; the shared jobs behind them are not theirs to count).</summary>
    public Task<int> CountStoredAsync(long chatId, CancellationToken ct) =>
        db.JobDeliveries.CountAsync(d => d.OwnerChatId == chatId, ct);

    /// <summary>Marks every stored vacancy of this user as not yet sent. Returns how many were reset. Other users' state is untouched.</summary>
    public async Task<Result<int, Error>> ResendAllAsync(long chatId, CancellationToken ct)
    {
        // Delivered must be cleared too: delivery treats "same Hash already Delivered"
        // as a duplicate, so every job would skip itself.
        return await db.JobDeliveries
            .Where(d => d.OwnerChatId == chatId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Processed, false)
                .SetProperty(d => d.Delivered, false), ct);
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
                return Errors.CheckAlreadyRunning;
            case RunStatus.Scheduled:
                return Errors.HourlyCheckRunning;
        }

        return await runner.RunAsync(chatId, ct);
    }
}
