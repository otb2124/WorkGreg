using System.Text.Json.Serialization;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Client;

/// <summary>
/// Everything the bot needs to know about the catalog and the input limits. Not per user, so a client may cache it.
/// </summary>
public sealed record WorkGregMeta(
    IReadOnlyList<ProviderDto> Providers,
    IReadOnlyList<RegionDto> Regions,
    int MaxProfilesPerUser,
    int MaxNameLength,
    int MaxKeywordLength)
{
    /// <summary>Only providers that can actually be fetched right now.</summary>
    [JsonIgnore]
    public IEnumerable<ProviderDto> Available => Providers.Where(p => p.IsAvailable);

    /// <summary>"dou, djinni, remotive ..." for usage and help texts.</summary>
    [JsonIgnore]
    public string ProviderIds => string.Join(", ", Providers.Select(p => p.Id));
}

/// <summary>
/// The one door between a front end (the Telegram bot) and the job watcher. The bot talks only to this interface.
/// The bot process uses an HTTP implementation (calls the core's REST API); the core itself has an in-process one
/// (calls the services directly) that its API endpoints share.
/// Everything that can fail returns a <see cref="Result{T, TError}"/>. Profile results always carry the profile's sources
/// (empty for a profile that was just created or removed).
/// </summary>
public interface IWorkGregClient
{
    // ---- catalog and limits

    Task<WorkGregMeta> GetMetaAsync(CancellationToken ct);

    // ---- profiles

    Task<IReadOnlyList<ProfileDto>> ListProfilesAsync(long chatId, CancellationToken ct);
    Task<Result<ProfileDto, Error>> GetProfileAsync(long chatId, int id, CancellationToken ct);
    Task<Result<ProfileDto, Error>> CreateProfileAsync(long chatId, string keyword, CancellationToken ct);
    Task<Result<ProfileDto, Error>> RemoveProfileAsync(long chatId, int id, CancellationToken ct);
    Task<Result<ProfileDto, Error>> RenameProfileAsync(long chatId, int id, string? rawName, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetKeywordAsync(long chatId, int id, string? rawKeyword, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetEnabledAsync(long chatId, int id, bool enabled, CancellationToken ct);

    // ---- filters (empty / null clears)

    Task<Result<ProfileDto, Error>> SetIncludeAsync(long chatId, int id, string? csv, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetExcludeAsync(long chatId, int id, string? csv, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetCompaniesAsync(long chatId, int id, string? csv, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetLocationsAsync(long chatId, int id, string? csv, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetMinSalaryAsync(long chatId, int id, int? salary, CancellationToken ct);
    Task<Result<ProfileDto, Error>> SetRemoteOnlyAsync(long chatId, int id, bool remoteOnly, CancellationToken ct);

    // ---- sources of a profile (addressed by provider id)

    Task<Result<SourceChangeDto, Error>> AddSourceAsync(long chatId, int profileId, string? providerId, CancellationToken ct);
    Task<Result<SourceChangeDto, Error>> RemoveSourceAsync(long chatId, int profileId, string? providerId, CancellationToken ct);
    Task<Result<SourceChangeDto, Error>> ToggleSourceAsync(long chatId, int profileId, string providerId, CancellationToken ct);

    // ---- user settings
    Task<OverviewDto> GetOverviewAsync(long chatId, CancellationToken ct);
    Task<SettingsDto> GetSettingsAsync(long chatId, CancellationToken ct);
    Task<Result<SettingsDto, Error>> SetPausedAsync(long chatId, bool paused, CancellationToken ct);
    Task<Result<SettingsDto, Error>> SetDeliveryAsync(long chatId, DeliveryMode mode, int? digestHourUtc, CancellationToken ct);

    // ---- vacancies and checks

    Task<int> CountStoredJobsAsync(long chatId, CancellationToken ct);
    Task<Result<int, Error>> ResendAllAsync(long chatId, CancellationToken ct);

    /// <summary>
    /// Starts a check for this user and returns at once. Busy when this user's own check or the hourly check is running.
    /// Follow it with <see cref="GetCheckAsync"/> until <see cref="CheckStatusDto.Last"/> is set.
    /// </summary>
    Task<Result<Unit, Error>> StartCheckAsync(long chatId, CancellationToken ct);

    /// <summary>
    /// What is running right now from this user's point of view, whether this user's check is waiting for its turn,
    /// and how this user's last check ended (null while it is queued or running).
    /// </summary>
    Task<CheckStatusDto> GetCheckAsync(long chatId, CancellationToken ct);

    /// <summary>Cancels the check this user started (running, or still waiting for its turn). Never touches anybody else's run.</summary>
    Task<StopOutcome> RequestStopAsync(long chatId, CancellationToken ct);
}