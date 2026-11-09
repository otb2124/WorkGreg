using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;

namespace WorkGreg.Services;

/// <summary>
/// Cleans up in two steps. (1) Deletes deliveries that were handled a long time ago: only PROCESSED deliveries are deleted
/// (delivered, filtered out or duplicate), judged by when that user's source first stored them. Deliveries still waiting
/// to be sent are never touched. (2) Deletes stored vacancies that no delivery points at any more and that were first
/// seen before the window. A vacancy another user still has pending (or recently handled) stays.
///
/// A deleted vacancy is forgotten, so a posting that is still listed after the retention window would come back as
/// "new". <see cref="SourceSyncService"/> prevents that for postings that carry a date: it does not store anything
/// posted before the window. Undated postings (some feeds) can reappear if the site keeps them listed longer than
/// <see cref="RetentionOptions.JobRetentionDays"/>, which is why the default is long.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    TimeProvider time,
    ILogger<RetentionService> log)
{
    private const int BatchSize = 500;

    /// <summary>Returns how many deliveries were deleted. 0 and no database access when retention is switched off.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = options.Value.CutoffUtc(time.GetUtcNow().UtcDateTime);
        if (cutoff is null) return 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // Small batches keep each write lock short, so a poll cycle running at the same time isn't blocked for long.
        var deliveries = 0;
        while (true)
        {
            var ids = await db.JobDeliveries
                .Where(d => d.Processed && d.CreatedUtc < cutoff.Value)
                .OrderBy(d => d.Id)
                .Select(d => d.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0) break;

            deliveries += await db.JobDeliveries.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize) break;
        }

        // Vacancies nobody references any more (their deliveries were purged just now, or their source or profile was removed).
        var jobs = 0;
        while (true)
        {
            var ids = await db.Jobs
                .Where(j => j.FirstSeenUtc < cutoff.Value && !db.JobDeliveries.Any(d => d.JobId == j.Id))
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0) break;

            // Re-checked in the delete itself: a poll cycle may have attached a delivery since the ids were read.
            jobs += await db.Jobs
                .Where(j => ids.Contains(j.Id) && !db.JobDeliveries.Any(d => d.JobId == j.Id))
                .ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize) break;
        }

        if (deliveries > 0 || jobs > 0)
            log.LogInformation(
                "Retention: deleted {Deliveries} processed deliveries and {Jobs} unreferenced vacancies first seen before {Cutoff:yyyy-MM-dd}",
                deliveries, jobs, cutoff);

        return deliveries;
    }
}