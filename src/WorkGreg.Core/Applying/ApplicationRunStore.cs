using Microsoft.EntityFrameworkCore;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Applying;

/// <summary>What the runner needs to know about an application row. Not the report or the screenshot.</summary>
public sealed record RunInfo(int Id, long OwnerChatId, int? ProfileId, string Url, string Title, string Company);

/// <summary>The fill request, or the reason it could not be built (<see cref="Problem"/> is shown to the user; it holds no entered values).</summary>
public sealed record RunInput(FillRequest? Request, string? Problem);

/// <summary>
/// The database side of an autofill run: load the row, claim it, build the request from the profile's settings, store the
/// outcome. Every status change is compare-and-set (ONE UPDATE with the allowed statuses in its WHERE clause, from
/// <see cref="ApplicationRules"/>), so a double enqueue, a cancel during a run or a restart can never produce a second run
/// or overwrite a newer state. Nothing here logs user-entered values.
/// </summary>
public sealed class ApplicationRunStore(AppDb db, AutofillService autofill, TimeProvider time)
{
    /// <summary>Statuses that mean "a run was asked for or happened". Tracker rows (applied / skipped) and cancelled ones do not use up the day.</summary>
    private static readonly ApplyStatus[] CountsForDailyLimit =
    [
        ApplyStatus.Queued, ApplyStatus.Filling, ApplyStatus.NeedsInput, ApplyStatus.Ready,
        ApplyStatus.Submitting, ApplyStatus.Submitted, ApplyStatus.Failed
    ];

    public Task<RunInfo?> LoadAsync(int applicationId, CancellationToken ct) =>
        db.Applications.AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => new RunInfo(a.Id, a.OwnerChatId, a.SearchProfileId, a.Url, a.Title, a.Company))
            .FirstOrDefaultAsync(ct);

    /// <summary>Queued -> Filling. False when the row is gone or no longer queued (cancelled, already running): then nothing may run.</summary>
    public async Task<bool> TryStartAsync(int applicationId, CancellationToken ct)
    {
        var allowedFrom = ApplicationRules.SourcesOf(ApplyStatus.Filling);
        var now = Now();
        var changed = await db.Applications
            .Where(a => a.Id == applicationId && allowedFrom.Contains(a.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApplyStatus.Filling)
                .SetProperty(a => a.UpdatedUtc, now), ct);
        return changed == 1;
    }

    /// <summary>
    /// Filling -> <paramref name="to"/> together with the outcome, in one guarded update. False when the status moved meanwhile
    /// (the user cancelled): the result is then dropped, never written over the newer state.
    /// </summary>
    public async Task<bool> TryFinishAsync(
        int applicationId, ApplyStatus to, AtsKind ats, string? reportJson, byte[]? screenshot, string? error, CancellationToken ct)
    {
        var allowedFrom = ApplicationRules.SourcesOf(to);
        if (allowedFrom.Length == 0) return false;

        var now = Now();
        var changed = await db.Applications
            .Where(a => a.Id == applicationId && allowedFrom.Contains(a.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, to)
                .SetProperty(a => a.Ats, ats)
                .SetProperty(a => a.ReportJson, reportJson)
                .SetProperty(a => a.Screenshot, screenshot)
                .SetProperty(a => a.Error, error)
                .SetProperty(a => a.UpdatedUtc, now), ct);
        return changed == 1;
    }

    /// <summary>The settings of the application's profile as a fill request. A profile without first name, last name and email is not ready.</summary>
    public async Task<RunInput> BuildInputAsync(RunInfo info, AtsKind ats, CancellationToken ct)
    {
        const string noProfile = "The search profile of this vacancy no longer exists.";
        if (info.ProfileId is not { } profileId) return new RunInput(null, noProfile);

        var settings = await autofill.GetSettingsAsync(info.OwnerChatId, profileId, ct);
        if (settings.IsFailure) return new RunInput(null, noProfile);

        var values = settings.Value.StandardFields
            .Where(f => f.Key is not null && f.Value.Length > 0)
            .ToDictionary(f => f.Key!.Value, f => f.Value);

        if (!values.ContainsKey(AutofillKey.FirstName) || !values.ContainsKey(AutofillKey.LastName) || !values.ContainsKey(AutofillKey.Email))
            return new RunInput(null, "The profile's autofill settings no longer have a first name, last name and email.");

        var rules = settings.Value.CustomRules
            .Where(r => r.Pattern is not null)
            .Select(r => new CustomRule(r.Id, r.MatchBy, r.Pattern!, r.IsRegex, r.Value, r.SortOrder))
            .ToList();

        var cv = await db.AutofillDocuments.AsNoTracking()
            .Where(d => d.SearchProfileId == profileId && d.Kind == AutofillDocumentKind.Cv)
            .Select(d => new FillDocument(d.FileName, d.ContentType, d.Content))
            .FirstOrDefaultAsync(ct);

        return new RunInput(new FillRequest(info.Url, ats, values, rules, cv, new FillJobContext(info.Title, info.Company)), null);
    }

    /// <summary>Runs this chat used today (UTC day), not counting <paramref name="excludingApplicationId"/> (the one about to be queued).</summary>
    public Task<int> CountRunsTodayAsync(long chatId, int excludingApplicationId, CancellationToken ct)
    {
        var dayStart = Now().Date;
        return db.Applications.CountAsync(a =>
            a.OwnerChatId == chatId && a.Id != excludingApplicationId &&
            a.UpdatedUtc >= dayStart && CountsForDailyLimit.Contains(a.Status), ct);
    }

    /// <summary>Startup: a row still in Filling belonged to a run that died with the previous process.</summary>
    public async Task<int> FailInterruptedAsync(CancellationToken ct)
    {
        var now = Now();
        return await db.Applications
            .Where(a => a.Status == ApplyStatus.Filling)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApplyStatus.Failed)
                .SetProperty(a => a.Error, "interrupted")
                .SetProperty(a => a.UpdatedUtc, now), ct);
    }

    /// <summary>Startup: queued applications whose in-memory queue entry was lost with the previous process.</summary>
    public Task<List<int>> QueuedIdsAsync(CancellationToken ct) =>
        db.Applications.AsNoTracking()
            .Where(a => a.Status == ApplyStatus.Queued)
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .ToListAsync(ct);

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}