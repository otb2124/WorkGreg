using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Client;

/// <summary>
/// <see cref="IWorkGregClient"/> on top of the services in the same process. Scoped, like the services it wraps.
/// Replaced by an HTTP client when the bot becomes its own process.
/// </summary>
public sealed class InProcessWorkGregClient(
    ProfileService profiles,
    SourceService sources,
    SettingsService settings,
    JobService jobs,
    PollRunner runner) : IWorkGregClient
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

    public async Task<IReadOnlyList<ProfileDto>> ListProfilesAsync(long chatId, CancellationToken ct) =>
        (await profiles.ListAsync(chatId, ct)).Select(d => d.ToDto()).ToList();

    public async Task<Result<ProfileDto, Error>> GetProfileAsync(long chatId, int id, CancellationToken ct) =>
        (await profiles.GetAsync(chatId, id, ct)).Map(d => d.ToDto());

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

    public Task<Result<PollResult, Error>> CheckNowAsync(long chatId, CancellationToken ct) =>
        jobs.CheckNowAsync(chatId, ct);

    public Task<RunStatus> GetCheckStatusAsync(long chatId, CancellationToken ct) =>
        Task.FromResult(runner.StatusFor(chatId));

    public Task<StopOutcome> RequestStopAsync(long chatId, CancellationToken ct) =>
        Task.FromResult(runner.RequestStop(chatId));

    // ---- helpers

    /// <summary>After a single-field change, read the profile again so the result carries its sources.</summary>
    private async Task<Result<ProfileDto, Error>> ReloadAsync(
        long chatId, Task<Result<SearchProfile, Error>> change, CancellationToken ct)
    {
        var changed = await change;
        if (changed.IsFailure) return changed.Error;

        return await GetProfileAsync(chatId, changed.Value.Id, ct);
    }
}