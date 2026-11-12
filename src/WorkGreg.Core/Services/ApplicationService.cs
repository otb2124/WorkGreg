using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Contracts;

namespace WorkGreg.Services;

/// <summary>
/// What tapping 🤖 AutoFill shows while there is no browser: what would be entered, what is missing, and a possible duplicate.
/// <paramref name="Values"/> are the stored standard values (ordered by key); <paramref name="Missing"/> the keys with no value.
/// <paramref name="Existing"/> is this user's most recent application for the same vacancy (company + title, on any board).
/// </summary>
public sealed record AutofillPreview(
    int DeliveryId,
    int ProfileId,
    string ProfileName,
    string Title,
    string Company,
    bool Ready,
    IReadOnlyList<AutofillField> Values,
    IReadOnlyList<AutofillKey> Missing,
    bool HasCv,
    Application? Existing);

/// <summary>
/// Use cases around <see cref="Application"/> rows: the applied / skipped tracker under every delivered vacancy and (from Phase 3
/// on) the autofill runs. A vacancy is addressed by its <see cref="JobDelivery"/> id (that is what the button carries); the
/// delivery must belong to the calling chat, and one that does not is answered exactly like one that is gone.
/// Status changes are compare-and-set (see <see cref="TryTransitionAsync"/>), so a double tap can never run or submit twice.
/// Nothing here logs user-entered values, and the screenshot / report columns are never loaded by these methods.
/// </summary>
public sealed class ApplicationService(AppDb db, AutofillService autofill, TimeProvider time)
{
    public const int MaxPageSize = 100;

    /// <summary>Everything about the delivery the use cases need, read in one query. Null for a delivery that is not this chat's.</summary>
    private sealed record DeliveryInfo(
        int DeliveryId, int JobId, string Hash, string Title, string Company, string Url, int ProfileId, string ProfileName);

    /// <summary>An application without its two big columns (screenshot, report), for everything that only needs the row's state.</summary>
    private static readonly Expression<Func<Application, Application>> WithoutBlobs = a => new Application
    {
        Id = a.Id,
        OwnerChatId = a.OwnerChatId,
        SearchProfileId = a.SearchProfileId,
        JobId = a.JobId,
        JobHash = a.JobHash,
        Title = a.Title,
        Company = a.Company,
        Url = a.Url,
        Ats = a.Ats,
        Status = a.Status,
        Error = a.Error,
        CreatedUtc = a.CreatedUtc,
        UpdatedUtc = a.UpdatedUtc,
        SubmittedUtc = a.SubmittedUtc
    };

    // ---- reads

    public async Task<Result<Application, Error>> GetAsync(long chatId, int applicationId, CancellationToken ct)
    {
        var application = await db.Applications.AsNoTracking()
            .Where(a => a.Id == applicationId && a.OwnerChatId == chatId)
            .Select(WithoutBlobs)
            .FirstOrDefaultAsync(ct);

        if (application is null) return Errors.ApplicationNotFound;
        return application;
    }

    /// <summary>This chat's applications, newest first, optionally of one status. Skip / Take are clamped.</summary>
    public async Task<IReadOnlyList<Application>> ListAsync(
        long chatId, ApplyStatus? status, int skip, int take, CancellationToken ct)
    {
        var query = db.Applications.AsNoTracking().Where(a => a.OwnerChatId == chatId);
        if (status is { } s) query = query.Where(a => a.Status == s);

        return await query
            .OrderByDescending(a => a.UpdatedUtc).ThenByDescending(a => a.Id)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .Select(WithoutBlobs)
            .ToListAsync(ct);
    }

    // ---- tapping 🤖 AutoFill (preview until the browser exists)

    /// <summary>
    /// Read-only: nothing is stored, so tapping the button twice, or abandoning the preview, leaves no half-started application behind.
    /// A profile that lost its first name, last name or email since the vacancy was delivered comes back with Ready = false.
    /// </summary>
    public async Task<Result<AutofillPreview, Error>> PreviewAsync(long chatId, int deliveryId, CancellationToken ct)
    {
        var info = await LoadDeliveryAsync(chatId, deliveryId, ct);
        if (info is null) return Errors.DeliveryNotFound;

        var settings = await autofill.GetSettingsAsync(chatId, info.ProfileId, ct);
        if (settings.IsFailure) return settings.Error;

        var values = settings.Value.StandardFields.Where(f => f.Key is not null && f.Value.Length > 0).ToList();
        var set = values.Select(f => f.Key!.Value).ToHashSet();

        var ready = set.Contains(AutofillKey.FirstName) && set.Contains(AutofillKey.LastName) && set.Contains(AutofillKey.Email);
        var missing = Enum.GetValues<AutofillKey>().Where(k => !set.Contains(k)).ToList();
        var hasCv = settings.Value.Documents.Any(d => d.Kind == AutofillDocumentKind.Cv);

        // Same company + title on any board counts (JobHash is copied from the job, so this survives the job being purged).
        var existing = await db.Applications.AsNoTracking()
            .Where(a => a.OwnerChatId == chatId && a.JobHash == info.Hash)
            .OrderByDescending(a => a.UpdatedUtc).ThenByDescending(a => a.Id)
            .Select(WithoutBlobs)
            .FirstOrDefaultAsync(ct);

        return new AutofillPreview(
            info.DeliveryId, info.ProfileId, info.ProfileName, info.Title, info.Company,
            ready, values, missing, hasCv, existing);
    }

    // ---- the tracker buttons

    /// <summary>✅ Applied. Creates the row (no application -> AppliedManually). Marking an already applied vacancy again is fine.</summary>
    public Task<Result<Application, Error>> MarkAppliedAsync(long chatId, int deliveryId, CancellationToken ct) =>
        TrackAsync(chatId, deliveryId, ApplyStatus.AppliedManually, ct);

    /// <summary>⏭ Skip. Creates the row (no application -> Skipped). Skipping an already skipped vacancy again is fine.</summary>
    public Task<Result<Application, Error>> SkipAsync(long chatId, int deliveryId, CancellationToken ct) =>
        TrackAsync(chatId, deliveryId, ApplyStatus.Skipped, ct);

    private async Task<Result<Application, Error>> TrackAsync(
        long chatId, int deliveryId, ApplyStatus target, CancellationToken ct)
    {
        var created = await GetOrCreateAsync(chatId, deliveryId, target, ct);
        if (created.IsFailure) return created.Error;

        // The row already existed in another state (skipped it, then "applied"; or an autofill run is going): a terminal or running
        // row is not overwritten by a tracker tap.
        var application = created.Value;
        if (application.Status != target)
            return Errors.ApplicationStateConflict(ApplyStatusLabels.Of(application.Status));

        return application;
    }

    /// <summary>
    /// The application of this chat for this vacancy, created with <paramref name="initial"/> when there is none yet. An existing
    /// row is returned as it is, whatever its status. At most one row exists per (chat, job): when two taps race, the unique
    /// index lets one create it and the other reads that one.
    /// </summary>
    public async Task<Result<Application, Error>> GetOrCreateAsync(
        long chatId, int deliveryId, ApplyStatus initial, CancellationToken ct)
    {
        var info = await LoadDeliveryAsync(chatId, deliveryId, ct);
        if (info is null) return Errors.DeliveryNotFound;

        var existing = await FindByJobAsync(chatId, info.JobId, ct);
        if (existing is not null) return existing;

        var now = time.GetUtcNow().UtcDateTime;
        var application = new Application
        {
            OwnerChatId = chatId,
            SearchProfileId = info.ProfileId,
            JobId = info.JobId,
            JobHash = info.Hash,
            Title = info.Title,
            Company = info.Company,
            Url = info.Url,
            Ats = AtsKind.Unknown,   // detected by the Applier (Phase 3)
            Status = initial,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        db.Applications.Add(application);

        try
        {
            await db.SaveChangesAsync(ct);
            return application;
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique (chat, job) index: the other tap's row is the answer.
            db.ChangeTracker.Clear();
            var winner = await FindByJobAsync(chatId, info.JobId, ct);
            if (winner is null) throw;
            return winner;
        }
    }

    // ---- status changes

    /// <summary>
    /// Moves an application to <paramref name="to"/> if (and only if) it is in a status the lifecycle allows that from
    /// (<see cref="ApplicationRules"/>). Compare-and-set: ONE UPDATE with the status in its WHERE clause, so of two simultaneous
    /// calls exactly one returns true and the other changes nothing (a double tap cannot run or submit twice).
    /// </summary>
    /// <returns>True when this call made the change.</returns>
    public async Task<bool> TryTransitionAsync(long chatId, int applicationId, ApplyStatus to, CancellationToken ct)
    {
        var allowedFrom = ApplicationRules.SourcesOf(to);
        if (allowedFrom.Length == 0) return false;

        var now = time.GetUtcNow().UtcDateTime;
        var rows = db.Applications.Where(a => a.Id == applicationId && a.OwnerChatId == chatId && allowedFrom.Contains(a.Status));

        var changed = to == ApplyStatus.Submitted
            ? await rows.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, to)
                .SetProperty(a => a.UpdatedUtc, now)
                .SetProperty(a => a.SubmittedUtc, (DateTime?)now), ct)
            : await rows.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, to)
                .SetProperty(a => a.UpdatedUtc, now), ct);

        return changed == 1;
    }

    // ---- helpers

    private Task<Application?> FindByJobAsync(long chatId, int jobId, CancellationToken ct) =>
        db.Applications.AsNoTracking()
            .Where(a => a.OwnerChatId == chatId && a.JobId == jobId)
            .Select(WithoutBlobs)
            .FirstOrDefaultAsync(ct);

    private Task<DeliveryInfo?> LoadDeliveryAsync(long chatId, int deliveryId, CancellationToken ct) =>
        (from d in db.JobDeliveries.AsNoTracking()
         join s in db.Sources.AsNoTracking() on d.SourceId equals s.Id
         where d.Id == deliveryId && d.OwnerChatId == chatId && s.SearchProfile.OwnerChatId == chatId
         select new DeliveryInfo(
             d.Id, d.JobId, d.Job.Hash, d.Job.Title, d.Job.Company ?? "", d.Job.Url,
             s.SearchProfileId, s.SearchProfile.Name))
        .FirstOrDefaultAsync(ct);
}
