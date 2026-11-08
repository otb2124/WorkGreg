using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkGreg.Data;

namespace WorkGreg.Services;

/// <summary>
/// Deletes stored vacancies that were handled a long time ago. Only PROCESSED jobs are deleted (delivered, filtered
/// out or duplicate), judged by when we first saw them. Jobs still waiting to be sent are never touched.
///
/// A deleted job is forgotten, so a posting that is still listed after the retention window would come back as
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

    /// <summary>Returns how many jobs were deleted. 0 and no database access when retention is switched off.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = options.Value.CutoffUtc(time.GetUtcNow().UtcDateTime);
        if (cutoff is null) return 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // Small batches keep each write lock short, so a poll cycle running at the same time isn't blocked for long.
        var total = 0;
        while (true)
        {
            var ids = await db.Jobs
                .Where(j => j.Processed && j.FirstSeenUtc < cutoff.Value)
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0) break;

            total += await db.Jobs.Where(j => ids.Contains(j.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize) break;
        }

        if (total > 0)
            log.LogInformation("Retention: deleted {Count} processed vacancies first seen before {Cutoff:yyyy-MM-dd}", total, cutoff);

        return total;
    }
}