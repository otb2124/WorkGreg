using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Client;

/// <summary>
/// <see cref="IWorkGregClient"/> on top of the services in the same process. Scoped, like the services it wraps.
/// The bot no longer uses it (it talks to the core over HTTP); the core's own API endpoints do.
/// </summary>
public sealed class InProcessWorkGregClient(
    ProfileService profiles,
    SourceService sources,
    SettingsService settings,
    JobService jobs,
    AutofillService autofill,
    ApplicationService applications,
    ManualCheckTracker checks) : IWorkGregClient
{
    // ---- catalog and limits

    public Task<WorkGregMeta> GetMetaAsync(CancellationToken ct) =>
        Task.FromResult(new WorkGregMeta(
            sources.Providers().Select(p => p.ToDto()).ToList(),
            SourceCatalog.Regions.Select(r => new RegionDto(r.Id, r.Label)).ToList(),
            ProfileRules.MaxProfilesPerUser,
            ProfileRules.MaxNameLength,
            SourceCatalog.MaxKeywordLength));

    // ---- profiles

    public async Task<IReadOnlyList<ProfileDto>> ListProfilesAsync(long chatId, CancellationToken ct)
    {
        // Awaited one after the other: a scoped DbContext cannot run two queries at once.
        var details = await profiles.ListAsync(chatId, ct);
        var ready = await autofill.ReadyProfileIdsAsync(chatId, ct);
        return details.Select(d => d.ToDto(ready.Contains(d.Profile.Id))).ToList();
    }

    public async Task<Result<ProfileDto, Error>> GetProfileAsync(long chatId, int id, CancellationToken ct)
    {
        var details = await profiles.GetAsync(chatId, id, ct);
        if (details.IsFailure) return details.Error;

        var ready = await autofill.ReadyProfileIdsAsync(chatId, ct);
        return details.Value.ToDto(ready.Contains(id));
    }

    public async Task<Result<ProfileDto, Error>> CreateProfileAsync(long chatId, string keyword, CancellationToken ct) =>
        (await profiles.CreateAsync(chatId, new ProfileDraft(keyword), ct)).Map(p => p.ToDto([]));

    public async Task<Result<ProfileDto, Error>> RemoveProfileAsync(long chatId, int id, CancellationToken ct) =>
        (await profiles.RemoveAsync(chatId, id, ct)).Map(p => p.ToDto([]));

    public Task<Result<ProfileDto, Error>> RenameProfileAsync(long chatId, int id, string? rawName, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.RenameAsync(chatId, id, rawName, ct), ct);

    public Task<Result<ProfileDto, Error>> SetKeywordAsync(long chatId, int id, string? rawKeyword, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetKeywordAsync(chatId, id, rawKeyword, ct), ct);

    public Task<Result<ProfileDto, Error>> SetEnabledAsync(long chatId, int id, bool enabled, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetEnabledAsync(chatId, id, enabled, ct), ct);

    // ---- filters

    public Task<Result<ProfileDto, Error>> SetIncludeAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetIncludeAsync(chatId, id, csv, ct), ct);

    public Task<Result<ProfileDto, Error>> SetExcludeAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetExcludeAsync(chatId, id, csv, ct), ct);

    public Task<Result<ProfileDto, Error>> SetCompaniesAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetCompaniesAsync(chatId, id, csv, ct), ct);

    public Task<Result<ProfileDto, Error>> SetLocationsAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetLocationsAsync(chatId, id, csv, ct), ct);

    public Task<Result<ProfileDto, Error>> SetMinSalaryAsync(long chatId, int id, int? salary, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetMinSalaryAsync(chatId, id, salary, ct), ct);

    public Task<Result<ProfileDto, Error>> SetRemoteOnlyAsync(long chatId, int id, bool remoteOnly, CancellationToken ct) =>
        ReloadAsync(chatId, profiles.SetRemoteOnlyAsync(chatId, id, remoteOnly, ct), ct);

    // ---- autofill

    public async Task<Result<AutofillSettingsDto, Error>> GetAutofillAsync(long chatId, int profileId, CancellationToken ct) =>
        (await autofill.GetSettingsAsync(chatId, profileId, ct)).Map(s => s.ToDto());

    public Task<Result<AutofillSettingsDto, Error>> SetAutofillValueAsync(
        long chatId, int profileId, AutofillKey key, string? value, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.SetStandardFieldAsync(chatId, profileId, key, value, ct), ct);

    public Task<Result<AutofillSettingsDto, Error>> ClearAutofillValueAsync(
        long chatId, int profileId, AutofillKey key, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.ClearStandardFieldAsync(chatId, profileId, key, ct), ct);

    public Task<Result<AutofillSettingsDto, Error>> AddAutofillRuleAsync(
        long chatId, int profileId, AutofillRuleRequest rule, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.AddCustomRuleAsync(chatId, profileId, ToDraft(rule), ct), ct);

    public Task<Result<AutofillSettingsDto, Error>> UpdateAutofillRuleAsync(
        long chatId, int profileId, int ruleId, AutofillRuleRequest rule, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.UpdateCustomRuleAsync(chatId, profileId, ruleId, ToDraft(rule), ct), ct);

    public Task<Result<AutofillSettingsDto, Error>> DeleteAutofillRuleAsync(
        long chatId, int profileId, int ruleId, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.DeleteCustomRuleAsync(chatId, profileId, ruleId, ct), ct);

    public Task<Result<AutofillSettingsDto, Error>> SetAutofillDocumentAsync(
        long chatId, int profileId, AutofillDocumentKind kind, string fileName, byte[] content, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.SetDocumentAsync(chatId, profileId, kind, fileName, content, ct), ct);

    public Task<Result<AutofillSettingsDto, Error>> DeleteAutofillDocumentAsync(
        long chatId, int profileId, AutofillDocumentKind kind, CancellationToken ct) =>
        ReloadAutofillAsync(chatId, profileId, autofill.DeleteDocumentAsync(chatId, profileId, kind, ct), ct);

    public async Task<Result<AutofillSettingsDto, Error>> CopyAutofillAsync(
        long chatId, int fromProfileId, int toProfileId, CancellationToken ct) =>
        (await autofill.CopySettingsAsync(chatId, fromProfileId, toProfileId, ct)).Map(s => s.ToDto());

    // ---- applications

    public async Task<Result<AutofillPreviewDto, Error>> StartAutofillAsync(long chatId, int deliveryId, CancellationToken ct) =>
        (await applications.PreviewAsync(chatId, deliveryId, ct)).Map(p => p.ToDto());

    public async Task<Result<ApplicationDto, Error>> MarkAppliedAsync(long chatId, int deliveryId, CancellationToken ct) =>
        (await applications.MarkAppliedAsync(chatId, deliveryId, ct)).Map(a => a.ToDto());

    public async Task<Result<ApplicationDto, Error>> SkipAsync(long chatId, int deliveryId, CancellationToken ct) =>
        (await applications.SkipAsync(chatId, deliveryId, ct)).Map(a => a.ToDto());

    public async Task<Result<ApplicationDto, Error>> GetApplicationAsync(long chatId, int applicationId, CancellationToken ct) =>
        (await applications.GetAsync(chatId, applicationId, ct)).Map(a => a.ToDto());

    // ---- sources

    public async Task<Result<SourceChangeDto, Error>> AddSourceAsync(
        long chatId, int profileId, string? providerId, CancellationToken ct) =>
        (await sources.AddAsync(chatId, profileId, providerId, ct)).Map(c => c.ToDto());

    public async Task<Result<SourceChangeDto, Error>> RemoveSourceAsync(
        long chatId, int profileId, string? providerId, CancellationToken ct) =>
        (await sources.RemoveByProviderAsync(chatId, profileId, providerId, ct)).Map(c => c.ToDto());

    public async Task<Result<SourceChangeDto, Error>> ToggleSourceAsync(
        long chatId, int profileId, string providerId, CancellationToken ct) =>
        (await sources.ToggleAsync(chatId, profileId, providerId, ct)).Map(c => c.ToDto());

    // ---- user settings
    public async Task<OverviewDto> GetOverviewAsync(long chatId, CancellationToken ct)
    {
        // One scoped DbContext cannot run two queries at once, so these are awaited one after the other.
        var user = await GetSettingsAsync(chatId, ct);
        var all = await ListProfilesAsync(chatId, ct);
        return new OverviewDto(user, all);
    }
    public async Task<SettingsDto> GetSettingsAsync(long chatId, CancellationToken ct) =>
        (await settings.GetAsync(chatId, ct)).ToDto();

    public async Task<Result<SettingsDto, Error>> SetPausedAsync(long chatId, bool paused, CancellationToken ct) =>
        (await settings.SetPausedAsync(chatId, paused, ct)).Map(s => s.ToDto());

    public async Task<Result<SettingsDto, Error>> SetDeliveryAsync(
        long chatId, DeliveryMode mode, int? digestHourUtc, CancellationToken ct) =>
        (await settings.SetDeliveryAsync(chatId, mode, digestHourUtc, ct)).Map(s => s.ToDto());

    // ---- vacancies and checks

    public Task<int> CountStoredJobsAsync(long chatId, CancellationToken ct) =>
        jobs.CountStoredAsync(chatId, ct);

    public Task<Result<int, Error>> ResendAllAsync(long chatId, CancellationToken ct) =>
        jobs.ResendAllAsync(chatId, ct);

    public Task<Result<Unit, Error>> StartCheckAsync(long chatId, CancellationToken ct) =>
        Task.FromResult(checks.Start(chatId));

    public Task<CheckStatusDto> GetCheckAsync(long chatId, CancellationToken ct) =>
        Task.FromResult(checks.Get(chatId).ToDto());

    public Task<StopOutcome> RequestStopAsync(long chatId, CancellationToken ct) =>
        Task.FromResult(checks.Stop(chatId));

    // ---- helpers

    /// <summary>After a single-field change, read the profile again so the result carries its sources.</summary>
    private async Task<Result<ProfileDto, Error>> ReloadAsync(
        long chatId, Task<Result<SearchProfile, Error>> change, CancellationToken ct)
    {
        var changed = await change;
        if (changed.IsFailure) return changed.Error;

        return await GetProfileAsync(chatId, changed.Value.Id, ct);
    }

    /// <summary>After an autofill change, read the settings again so the answer is the state afterwards.</summary>
    private async Task<Result<AutofillSettingsDto, Error>> ReloadAutofillAsync<T>(
        long chatId, int profileId, Task<Result<T, Error>> change, CancellationToken ct)
    {
        var changed = await change;
        if (changed.IsFailure) return changed.Error;

        return await GetAutofillAsync(chatId, profileId, ct);
    }

    private static CustomRuleDraft ToDraft(AutofillRuleRequest r) => new(r.Pattern, r.Value, r.IsRegex, r.MatchBy);
}
