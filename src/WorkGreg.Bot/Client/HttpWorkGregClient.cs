using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Client;

/// <summary>
/// The core is unreachable or answered something this client cannot turn into a <see cref="Error"/>
/// (wrong API key, chat not allowed on the core, a server error, a broken URL). Expected failures such as
/// "no such profile" are never exceptions: they come back as a failed <see cref="Result{T, TError}"/>.
/// </summary>
public sealed class CoreApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = status;
}

/// <summary>
/// <see cref="IWorkGregClient"/> over the core's REST API. Typed HttpClient: base URL, X-Api-Key and the retry policy are
/// set where it is registered (AddWorkGregHttpClient). A 400/404/409 with an <c>{ kind, message }</c> body is the core's
/// <see cref="Error"/>; every other failure throws <see cref="CoreApiException"/> or the HttpClient's own exception,
/// which the bot's dispatcher logs.
/// </summary>
internal sealed class HttpWorkGregClient(HttpClient reads, HttpClient writes, MetaCache meta) : IWorkGregClient
{
    // Same settings as the server: camelCase, enums as strings.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ---- catalog and limits

    public Task<WorkGregMeta> GetMetaAsync(CancellationToken ct) =>
        meta.GetAsync(async c => Unwrap(await SendAsync<WorkGregMeta>(HttpMethod.Get, "api/meta", null, c)), ct);

    // ---- profiles

    public async Task<IReadOnlyList<ProfileDto>> ListProfilesAsync(long chatId, CancellationToken ct) =>
        Unwrap(await SendAsync<List<ProfileDto>>(HttpMethod.Get, $"{Chat(chatId)}/profiles", null, ct));

    public Task<Result<ProfileDto, Error>> GetProfileAsync(long chatId, int id, CancellationToken ct) =>
        SendAsync<ProfileDto>(HttpMethod.Get, $"{Chat(chatId)}/profiles/{id}", null, ct);

    public Task<Result<ProfileDto, Error>> CreateProfileAsync(long chatId, string keyword, CancellationToken ct) =>
        SendAsync<ProfileDto>(HttpMethod.Post, $"{Chat(chatId)}/profiles",
            new ProfileRequest(null, keyword, true, null, null, null, null, null, false), ct);

    public Task<Result<ProfileDto, Error>> RemoveProfileAsync(long chatId, int id, CancellationToken ct) =>
        SendAsync<ProfileDto>(HttpMethod.Delete, $"{Chat(chatId)}/profiles/{id}", null, ct);

    public Task<Result<ProfileDto, Error>> RenameProfileAsync(long chatId, int id, string? rawName, CancellationToken ct) =>
        PatchProfile(chatId, id, "name", rawName, ct);

    public Task<Result<ProfileDto, Error>> SetKeywordAsync(long chatId, int id, string? rawKeyword, CancellationToken ct) =>
        PatchProfile(chatId, id, "keyword", rawKeyword, ct);

    public Task<Result<ProfileDto, Error>> SetEnabledAsync(long chatId, int id, bool enabled, CancellationToken ct) =>
        PatchProfile(chatId, id, "enabled", enabled, ct);

    // ---- filters

    public Task<Result<ProfileDto, Error>> SetIncludeAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        PatchProfile(chatId, id, "include", csv, ct);

    public Task<Result<ProfileDto, Error>> SetExcludeAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        PatchProfile(chatId, id, "exclude", csv, ct);

    public Task<Result<ProfileDto, Error>> SetCompaniesAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        PatchProfile(chatId, id, "companies", csv, ct);

    public Task<Result<ProfileDto, Error>> SetLocationsAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        PatchProfile(chatId, id, "locations", csv, ct);

    public Task<Result<ProfileDto, Error>> SetMinSalaryAsync(long chatId, int id, int? salary, CancellationToken ct) =>
        PatchProfile(chatId, id, "minSalary", salary, ct);

    public Task<Result<ProfileDto, Error>> SetRemoteOnlyAsync(long chatId, int id, bool remoteOnly, CancellationToken ct) =>
        PatchProfile(chatId, id, "remoteOnly", remoteOnly, ct);

    // ---- sources (addressed by provider id)

    public Task<Result<SourceChangeDto, Error>> AddSourceAsync(long chatId, int profileId, string? providerId, CancellationToken ct) =>
        SourceCall(HttpMethod.Post, chatId, profileId, providerId, "", ct);

    public Task<Result<SourceChangeDto, Error>> RemoveSourceAsync(long chatId, int profileId, string? providerId, CancellationToken ct) =>
        SourceCall(HttpMethod.Delete, chatId, profileId, providerId, "", ct);

    public Task<Result<SourceChangeDto, Error>> ToggleSourceAsync(long chatId, int profileId, string providerId, CancellationToken ct) =>
        SourceCall(HttpMethod.Post, chatId, profileId, providerId, "/toggle", ct);

    // ---- user settings

    public async Task<SettingsDto> GetSettingsAsync(long chatId, CancellationToken ct) =>
        Unwrap(await SendAsync<SettingsDto>(HttpMethod.Get, $"{Chat(chatId)}/settings", null, ct));

    public Task<Result<SettingsDto, Error>> SetPausedAsync(long chatId, bool paused, CancellationToken ct) =>
        SendAsync<SettingsDto>(HttpMethod.Patch, $"{Chat(chatId)}/settings", new PatchSettingsRequest(paused, null, null), ct);

    public Task<Result<SettingsDto, Error>> SetDeliveryAsync(long chatId, DeliveryMode mode, int? digestHourUtc, CancellationToken ct) =>
        SendAsync<SettingsDto>(HttpMethod.Patch, $"{Chat(chatId)}/settings", new PatchSettingsRequest(null, mode, digestHourUtc), ct);

    // ---- vacancies and checks

    public async Task<int> CountStoredJobsAsync(long chatId, CancellationToken ct) =>
        Unwrap(await SendAsync<JobCountDto>(HttpMethod.Get, $"{Chat(chatId)}/jobs/count", null, ct)).Count;

    public async Task<Result<int, Error>> ResendAllAsync(long chatId, CancellationToken ct) =>
        (await SendAsync<ResendDto>(HttpMethod.Post, $"{Chat(chatId)}/resend", null, ct)).Map(r => r.Reset);

    public Task<Result<Unit, Error>> StartCheckAsync(long chatId, CancellationToken ct) =>
        SendAsync<Unit>(HttpMethod.Post, $"{Chat(chatId)}/check", null, ct);

    public async Task<CheckStatusDto> GetCheckAsync(long chatId, CancellationToken ct) =>
        Unwrap(await SendAsync<CheckStatusDto>(HttpMethod.Get, $"{Chat(chatId)}/check", null, ct));

    public async Task<StopOutcome> RequestStopAsync(long chatId, CancellationToken ct) =>
        Unwrap(await SendAsync<StopCheckDto>(HttpMethod.Post, $"{Chat(chatId)}/check/stop", null, ct)).Outcome;

    // ---- plumbing

    private static string Chat(long chatId) => $"api/{chatId}";

    /// <summary>PATCH /profiles/{id} takes exactly one field. A null value clears a filter (the core decides what null means for the others).</summary>
    private Task<Result<ProfileDto, Error>> PatchProfile(long chatId, int id, string field, object? value, CancellationToken ct) =>
        SendAsync<ProfileDto>(HttpMethod.Patch, $"{Chat(chatId)}/profiles/{id}",
            new Dictionary<string, object?> { [field] = value }, ct);

    private Task<Result<SourceChangeDto, Error>> SourceCall(
        HttpMethod method, long chatId, int profileId, string? providerId, string suffix, CancellationToken ct)
    {
        // An empty id would make the URL a different route; say what is wrong instead.
        var provider = providerId?.Trim();
        if (string.IsNullOrEmpty(provider))
            return Task.FromResult<Result<SourceChangeDto, Error>>(Error.Validation("Name a source."));

        return SendAsync<SourceChangeDto>(method,
            $"{Chat(chatId)}/profiles/{profileId}/sources/by-provider/{Uri.EscapeDataString(provider)}{suffix}", null, ct);
    }

    private async Task<Result<T, Error>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        // GET goes through the client that retries; everything else through the one that never does (see AddWorkGregHttpClient).
        var http = method == HttpMethod.Get ? reads : writes;
        using var response = await http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            // Nothing to read: success is the answer (starting a check answers 202 with a status nobody needs here).
            if (typeof(T) == typeof(Unit)) return (T)(object)Unit.Value;

            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            if (value is null) throw new CoreApiException(response.StatusCode, "The core sent an empty answer.");
            return value;
        }

        if (await TryReadErrorAsync(response, ct) is { } error) return error;
        throw Failure(response.StatusCode);
    }

    /// <summary>The core's { kind, message } body. Null for anything else, e.g. a 404 from a wrong URL or a framework 400.</summary>
    private static async Task<Error?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict))
            return null;

        try
        {
            var error = await response.Content.ReadFromJsonAsync<Error>(Json, ct);
            return error is { Message: not null } ? error : null;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }   // the body is not JSON
    }

    private static CoreApiException Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new(status, "The core rejected the API key. Core:ApiKey must equal the core's Api:ApiKey."),
        HttpStatusCode.Forbidden => new(status, "The core does not allow this chat. Check Bot:Access and Bot:AllowedChatIds on the core."),
        _ => new(status, $"The core answered {(int)status} {status}.")
    };

    /// <summary>For calls that cannot fail in a way the user should see: a failed result here means client and core disagree.</summary>
    private static T Unwrap<T>(Result<T, Error> result) =>
        result.IsSuccess
            ? result.Value
            : throw new CoreApiException(HttpStatusCode.BadRequest, $"The core refused the request: {result.Error.Message}");
}
