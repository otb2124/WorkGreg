using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Api;

/// <summary>
/// HTTP adapter: binds the request, calls a service, renders the result as a DTO / status code.
/// No validation or persistence lives here.
/// </summary>
public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();

        api.MapGet("/meta", GetMeta);
        api.MapGet("/fetch-types", GetFetchTypes);
        api.MapGet("/providers", GetProviders);

        var user = api.MapGroup("/{chatId:long}").AddEndpointFilter<ChatAccessFilter>();

        user.MapGet("/summary", GetSummary);

        user.MapGet("/settings", GetSettings);
        user.MapPut("/settings", SetSettings);
        user.MapPatch("/settings", PatchSettings);

        user.MapGet("/profiles", ListProfiles);
        user.MapPost("/profiles", AddProfile);
        user.MapGet("/profiles/{id:int}", GetProfile);
        user.MapPut("/profiles/{id:int}", UpdateProfile);
        user.MapPatch("/profiles/{id:int}", PatchProfile);
        user.MapDelete("/profiles/{id:int}", RemoveProfile);

        // Autofill settings of a profile (all inside the /{chatId} group, so ChatAccessFilter applies; the service checks ownership).
        user.MapGet("/profiles/{id:int}/autofill", GetAutofill);
        user.MapPut("/profiles/{id:int}/autofill/fields/{key}", SetAutofillValue);
        user.MapDelete("/profiles/{id:int}/autofill/fields/{key}", ClearAutofillValue);
        user.MapPost("/profiles/{id:int}/autofill/rules", AddAutofillRule);
        user.MapPut("/profiles/{id:int}/autofill/rules/{ruleId:int}", UpdateAutofillRule);
        user.MapDelete("/profiles/{id:int}/autofill/rules/{ruleId:int}", DeleteAutofillRule);
        user.MapPost("/profiles/{id:int}/autofill/documents/{kind}", SetAutofillDocument);
        user.MapDelete("/profiles/{id:int}/autofill/documents/{kind}", DeleteAutofillDocument);
        user.MapPost("/profiles/{id:int}/autofill/copy", CopyAutofill);

        // The buttons under a delivered vacancy. The vacancy is addressed by its delivery id; the service checks it is this chat's.
        user.MapPost("/deliveries/{deliveryId:int}/autofill", StartAutofill);
        user.MapPost("/deliveries/{deliveryId:int}/applied", MarkApplied);
        user.MapPost("/deliveries/{deliveryId:int}/skip", SkipVacancy);
        user.MapGet("/applications/{applicationId:int}", GetApplication);

        user.MapPost("/profiles/{id:int}/sources", AddSource);
        user.MapPatch("/profiles/{id:int}/sources/{sourceId:int}", UpdateSource);
        user.MapDelete("/profiles/{id:int}/sources/{sourceId:int}", RemoveSource);

        // The same operations addressed by provider id, the way the bot does it. They answer with SourceChangeDto.
        user.MapPost("/profiles/{id:int}/sources/by-provider/{providerId}", AddSourceByProvider);
        user.MapPost("/profiles/{id:int}/sources/by-provider/{providerId}/toggle", ToggleSourceByProvider);
        user.MapDelete("/profiles/{id:int}/sources/by-provider/{providerId}", RemoveSourceByProvider);

        user.MapGet("/jobs", ListJobs);
        user.MapGet("/jobs/count", CountJobs);
        user.MapPost("/resend", Resend);

        user.MapPost("/check", StartCheck);
        user.MapGet("/check", GetCheck);
        user.MapPost("/check/stop", StopCheck);

        user.MapGet("/overview", GetOverview);
    }

    // ---- Catalog

    /// <summary>Providers, regions and input limits in one call. Not per user, so a client may cache it.</summary>
    private static async Task<IResult> GetMeta(IWorkGregClient client, CancellationToken ct) =>
        Results.Ok(await client.GetMetaAsync(ct));

    private static IResult GetFetchTypes(SourceService sources) =>
        Results.Ok(sources.FetchTypes().Select(f => new FetchTypeDto(f.Type, f.IsAvailable)));

    private static IResult GetProviders(SourceService sources) =>
        Results.Ok(sources.Providers().Select(i => i.ToDto()));

    private static async Task<IResult> GetSummary(long chatId, JobService jobs, CancellationToken ct)
    {
        var s = await jobs.GetSummaryAsync(chatId, ct);
        return Results.Ok(new SummaryDto(
            s.Profiles, s.EnabledProfiles, s.Sources, s.EnabledSources, s.FailingSources,
            s.PendingJobs, s.DeliveredJobs, s.Paused));
    }

    // ---- Settings (user level)
    /// <summary>Settings and profiles together, so a client that shows both (the bot's main menu) makes one request.</summary>
    private static async Task<IResult> GetOverview(long chatId, IWorkGregClient client, CancellationToken ct) =>
        Results.Ok(await client.GetOverviewAsync(chatId, ct));
    private static async Task<IResult> GetSettings(long chatId, SettingsService settings, CancellationToken ct) =>
        Results.Ok((await settings.GetAsync(chatId, ct)).ToDto());

    private static async Task<IResult> SetSettings(
        long chatId, SettingsDto req, SettingsService settings, CancellationToken ct) =>
        (await settings.ReplaceAsync(chatId, req.Paused, req.Delivery, req.DigestHourUtc, ct))
            .ToHttp(s => Results.Ok(s.ToDto()));

    /// <summary>
    /// Changes one setting: <c>{ "paused": true }</c>, or <c>{ "delivery": "Digest", "digestHourUtc": 9 }</c>.
    /// Unlike PUT it validates the digest hour instead of clamping it.
    /// </summary>
    private static async Task<IResult> PatchSettings(
        long chatId, PatchSettingsRequest req, IWorkGregClient client, CancellationToken ct)
    {
        if (req.Delivery is null && req.DigestHourUtc is not null)
            return Errors.InvalidRequest("digestHourUtc only goes together with delivery.").ToHttp();

        if (req.Paused is not null && req.Delivery is not null)
            return Errors.InvalidRequest("Change one setting per PATCH: paused, or delivery. Use PUT to replace everything.").ToHttp();

        if (req.Paused is { } paused)
            return (await client.SetPausedAsync(chatId, paused, ct)).ToHttp(Results.Ok);

        if (req.Delivery is { } delivery)
            return (await client.SetDeliveryAsync(chatId, delivery, req.DigestHourUtc, ct)).ToHttp(Results.Ok);

        return Errors.InvalidRequest("Send paused, or delivery.").ToHttp();
    }

    // ---- Profiles

    // Listing and reading go through the client, which also fills ProfileDto.AutofillReady.
    private static async Task<IResult> ListProfiles(long chatId, IWorkGregClient client, CancellationToken ct) =>
        Results.Ok(await client.ListProfilesAsync(chatId, ct));

    private static async Task<IResult> GetProfile(long chatId, int id, IWorkGregClient client, CancellationToken ct) =>
        (await client.GetProfileAsync(chatId, id, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> AddProfile(
        long chatId, ProfileRequest req, ProfileService profiles, CancellationToken ct) =>
        (await profiles.CreateAsync(chatId, ToDraft(req), ct))
            .ToHttp(p => Results.Created($"/api/{chatId}/profiles/{p.Id}", p.ToDto([])));

    /// <summary>Replaces all profile fields at once. A changed keyword is pushed to every source of the profile.</summary>
    private static async Task<IResult> UpdateProfile(
        long chatId, int id, ProfileRequest req, ProfileService profiles, AutofillService autofill, CancellationToken ct)
    {
        var replaced = await profiles.ReplaceAsync(chatId, id, ToDraft(req), ct);
        if (replaced.IsFailure) return replaced.Error.ToHttp();

        var ready = await autofill.ReadyProfileIdsAsync(chatId, ct);
        return Results.Ok(replaced.Value.ToDto(ready.Contains(id)));
    }

    private const string PatchableFields = "name, keyword, enabled, include, exclude, companies, locations, minSalary, remoteOnly";

    /// <summary>
    /// Changes ONE field, e.g. <c>{ "include": ".NET, C#" }</c> or <c>{ "minSalary": null }</c> (null or "" clears a filter).
    /// Exactly one field per request, so a failure never leaves half a change behind; use PUT to change several at once.
    /// Each field goes through the same IWorkGregClient method the bot uses. Answers with the whole profile.
    /// </summary>
    private static async Task<IResult> PatchProfile(
        long chatId, int id, JsonElement body, IWorkGregClient client, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Errors.InvalidRequest($"Send a JSON object with exactly one field ({PatchableFields}).").ToHttp();

        var fields = body.EnumerateObject().ToList();
        if (fields.Count != 1)
            return Errors.InvalidRequest(
                $"Send exactly one field per PATCH ({PatchableFields}). Use PUT to change several at once.").ToHttp();

        var (name, value) = (fields[0].Name, fields[0].Value);

        Task<Result<ProfileDto, Error>> change = name.ToLowerInvariant() switch
        {
            "name" => WithText(value, name, s => client.RenameProfileAsync(chatId, id, s, ct)),
            "keyword" => WithText(value, name, s => client.SetKeywordAsync(chatId, id, s, ct)),
            "include" => WithCsv(value, name, s => client.SetIncludeAsync(chatId, id, s, ct)),
            "exclude" => WithCsv(value, name, s => client.SetExcludeAsync(chatId, id, s, ct)),
            "companies" => WithCsv(value, name, s => client.SetCompaniesAsync(chatId, id, s, ct)),
            "locations" => WithCsv(value, name, s => client.SetLocationsAsync(chatId, id, s, ct)),
            "minsalary" => WithNumber(value, name, n => client.SetMinSalaryAsync(chatId, id, n, ct)),
            "enabled" => WithFlag(value, name, b => client.SetEnabledAsync(chatId, id, b, ct)),
            "remoteonly" => WithFlag(value, name, b => client.SetRemoteOnlyAsync(chatId, id, b, ct)),
            _ => Invalid($"Unknown field '{name}'. Fields: {PatchableFields}.")
        };

        return (await change).ToHttp(Results.Ok);
    }

    private static Task<Result<ProfileDto, Error>> Invalid(string message) =>
        Task.FromResult<Result<ProfileDto, Error>>(Errors.InvalidRequest(message));

    /// <summary>A string, or null (the setter decides whether null is acceptable).</summary>
    private static Task<Result<ProfileDto, Error>> WithText(
        JsonElement v, string field, Func<string?, Task<Result<ProfileDto, Error>>> apply) =>
        v.ValueKind switch
        {
            JsonValueKind.Null => apply(null),
            JsonValueKind.String => apply(v.GetString()),
            _ => Invalid($"'{field}' must be a string.")
        };

    /// <summary>The service takes comma-separated text. A JSON array of strings is joined for convenience; null clears.</summary>
    private static Task<Result<ProfileDto, Error>> WithCsv(
        JsonElement v, string field, Func<string?, Task<Result<ProfileDto, Error>>> apply)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
                return apply(null);

            case JsonValueKind.String:
                return apply(v.GetString());

            case JsonValueKind.Array:
                {
                    var items = new List<string>();
                    foreach (var item in v.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            return Invalid($"'{field}' must be a comma-separated string, an array of strings, or null.");
                        items.Add(item.GetString()!);
                    }
                    return apply(string.Join(", ", items));
                }

            default:
                return Invalid($"'{field}' must be a comma-separated string, an array of strings, or null.");
        }
    }

    private static Task<Result<ProfileDto, Error>> WithNumber(
        JsonElement v, string field, Func<int?, Task<Result<ProfileDto, Error>>> apply) =>
        v.ValueKind switch
        {
            JsonValueKind.Null => apply(null),
            JsonValueKind.Number when v.TryGetInt32(out var n) => apply(n),
            _ => Invalid($"'{field}' must be a whole number, or null to clear it.")
        };

    private static Task<Result<ProfileDto, Error>> WithFlag(
        JsonElement v, string field, Func<bool, Task<Result<ProfileDto, Error>>> apply) =>
        v.ValueKind switch
        {
            JsonValueKind.True => apply(true),
            JsonValueKind.False => apply(false),
            _ => Invalid($"'{field}' must be true or false.")
        };

    /// <summary>Answers with the removed profile (its name is what a client shows in its confirmation).</summary>
    private static async Task<IResult> RemoveProfile(long chatId, int id, ProfileService profiles, CancellationToken ct) =>
        (await profiles.RemoveAsync(chatId, id, ct)).ToHttp(p => Results.Ok(p.ToDto([])));

    // ---- Autofill settings of a profile. Every call answers with the settings as they are afterwards.

    private static async Task<IResult> GetAutofill(long chatId, int id, IWorkGregClient client, CancellationToken ct) =>
        (await client.GetAutofillAsync(chatId, id, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> SetAutofillValue(
        long chatId, int id, string key, SetAutofillValueRequest req, IWorkGregClient client, CancellationToken ct)
    {
        if (ParseEnum<AutofillKey>(key) is not { } k) return UnknownKey(key);
        return (await client.SetAutofillValueAsync(chatId, id, k, req.Value, ct)).ToHttp(Results.Ok);
    }

    private static async Task<IResult> ClearAutofillValue(
        long chatId, int id, string key, IWorkGregClient client, CancellationToken ct)
    {
        if (ParseEnum<AutofillKey>(key) is not { } k) return UnknownKey(key);
        return (await client.ClearAutofillValueAsync(chatId, id, k, ct)).ToHttp(Results.Ok);
    }

    private static async Task<IResult> AddAutofillRule(
        long chatId, int id, AutofillRuleRequest req, IWorkGregClient client, CancellationToken ct) =>
        (await client.AddAutofillRuleAsync(chatId, id, req, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> UpdateAutofillRule(
        long chatId, int id, int ruleId, AutofillRuleRequest req, IWorkGregClient client, CancellationToken ct) =>
        (await client.UpdateAutofillRuleAsync(chatId, id, ruleId, req, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> DeleteAutofillRule(
        long chatId, int id, int ruleId, IWorkGregClient client, CancellationToken ct) =>
        (await client.DeleteAutofillRuleAsync(chatId, id, ruleId, ct)).ToHttp(Results.Ok);

    /// <summary>
    /// multipart/form-data with the file in a field named "file". The body is capped while it is read (the limit is set before
    /// the form is parsed), so an oversized upload is stopped early instead of being buffered. The service then checks the
    /// size again, the extension and the file's first bytes.
    /// </summary>
    private static async Task<IResult> SetAutofillDocument(
        long chatId, int id, string kind, HttpContext http, IWorkGregClient client, CancellationToken ct)
    {
        if (ParseEnum<AutofillDocumentKind>(kind) is not { } documentKind)
            return Errors.InvalidRequest($"Unknown document kind '{kind}'. Kinds: {string.Join(", ", Enum.GetNames<AutofillDocumentKind>())}.").ToHttp();

        var request = http.Request;
        if (!request.HasFormContentType)
            return Errors.InvalidRequest("Send the file as multipart/form-data in a field named 'file'.").ToHttp();

        // The file plus some room for the multipart framing.
        const long bodyLimit = AutofillLimits.MaxDocumentBytes + 64 * 1024;
        var tooLarge = Errors.AutofillDocumentTooLarge(AutofillLimits.MaxDocumentBytes / (1024 * 1024)).ToHttp();

        if (request.ContentLength > bodyLimit) return tooLarge;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = bodyLimit;

        IFormFile? file;
        try
        {
            file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        }
        catch (BadHttpRequestException)
        {
            return tooLarge;   // the body went over the limit while it was being read
        }
        catch (InvalidDataException)
        {
            return Errors.InvalidRequest("The upload could not be read.").ToHttp();
        }

        if (file is null) return Errors.InvalidRequest("Send the file in a form field named 'file'.").ToHttp();
        if (file.Length > AutofillLimits.MaxDocumentBytes) return tooLarge;

        var bytes = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(bytes, ct);

        return (await client.SetAutofillDocumentAsync(chatId, id, documentKind, file.FileName, bytes, ct)).ToHttp(Results.Ok);
    }

    private static async Task<IResult> DeleteAutofillDocument(
        long chatId, int id, string kind, IWorkGregClient client, CancellationToken ct)
    {
        if (ParseEnum<AutofillDocumentKind>(kind) is not { } documentKind)
            return Errors.InvalidRequest($"Unknown document kind '{kind}'. Kinds: {string.Join(", ", Enum.GetNames<AutofillDocumentKind>())}.").ToHttp();

        return (await client.DeleteAutofillDocumentAsync(chatId, id, documentKind, ct)).ToHttp(Results.Ok);
    }

    /// <summary>The route's {id} is the TARGET profile; its settings are replaced by a copy of the source's.</summary>
    private static async Task<IResult> CopyAutofill(
        long chatId, int id, CopyAutofillRequest req, IWorkGregClient client, CancellationToken ct) =>
        (await client.CopyAutofillAsync(chatId, req.FromProfileId, id, ct)).ToHttp(Results.Ok);

    /// <summary>A name from the URL to an enum value (case-insensitive). Numbers are refused: only names are part of the API.</summary>
    private static T? ParseEnum<T>(string? raw) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(raw) && !char.IsDigit(raw.Trim()[0]) && Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : null;

    private static IResult UnknownKey(string key) =>
        Errors.InvalidRequest($"Unknown autofill key '{key}'. Keys: {string.Join(", ", Enum.GetNames<AutofillKey>())}.").ToHttp();

    // ---- Applications (the tracker and the 🤖 AutoFill button under a vacancy)

    /// <summary>Until the browser exists this answers with a preview of what would be filled; nothing is stored.</summary>
    private static async Task<IResult> StartAutofill(long chatId, int deliveryId, IWorkGregClient client, CancellationToken ct) =>
        (await client.StartAutofillAsync(chatId, deliveryId, ct)).ToHttp(Results.Ok);

    /// <summary>Idempotent. 409 when the vacancy was skipped (or is in another state) already.</summary>
    private static async Task<IResult> MarkApplied(long chatId, int deliveryId, IWorkGregClient client, CancellationToken ct) =>
        (await client.MarkAppliedAsync(chatId, deliveryId, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> SkipVacancy(long chatId, int deliveryId, IWorkGregClient client, CancellationToken ct) =>
        (await client.SkipAsync(chatId, deliveryId, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> GetApplication(long chatId, int applicationId, IWorkGregClient client, CancellationToken ct) =>
        (await client.GetApplicationAsync(chatId, applicationId, ct)).ToHttp(Results.Ok);

    // ---- Sources of a profile

    private static async Task<IResult> AddSource(
        long chatId, int id, AddSourceRequest req, SourceService sources, CancellationToken ct) =>
        (await sources.AddAsync(chatId, id, req.Provider, ct))
            .ToHttp(c => Results.Created($"/api/{chatId}/profiles/{id}/sources/{c.Source.Id}", c.Source.ToDto()));

    private static async Task<IResult> UpdateSource(
        long chatId, int id, int sourceId, UpdateSourceRequest req, SourceService sources, CancellationToken ct) =>
        (await sources.UpdateAsync(chatId, id, sourceId, req.Enabled, ct)).ToHttp(s => Results.Ok(s.ToDto()));

    private static async Task<IResult> RemoveSource(
        long chatId, int id, int sourceId, SourceService sources, CancellationToken ct) =>
        (await sources.RemoveAsync(chatId, id, sourceId, ct)).ToHttp(_ => Results.NoContent());

    private static async Task<IResult> AddSourceByProvider(
        long chatId, int id, string providerId, IWorkGregClient client, CancellationToken ct) =>
        (await client.AddSourceAsync(chatId, id, providerId, ct))
            .ToHttp(c => Results.Created($"/api/{chatId}/profiles/{id}", c));

    /// <summary>Attaches the provider if the profile lacks it, detaches it otherwise. The body says which happened (Added).</summary>
    private static async Task<IResult> ToggleSourceByProvider(
        long chatId, int id, string providerId, IWorkGregClient client, CancellationToken ct) =>
        (await client.ToggleSourceAsync(chatId, id, providerId, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> RemoveSourceByProvider(
        long chatId, int id, string providerId, IWorkGregClient client, CancellationToken ct) =>
        (await client.RemoveSourceAsync(chatId, id, providerId, ct)).ToHttp(Results.Ok);

    // ---- Jobs

    private static async Task<IResult> ListJobs(
        long chatId, JobService jobs, CancellationToken ct,
        int? profileId = null, int? sourceId = null, bool? delivered = null, string? q = null, int skip = 0, int take = 50)
    {
        var page = await jobs.ListAsync(chatId, new JobQuery(profileId, sourceId, delivered, q, skip, take), ct);

        var items = page.Items.Select(i => new JobDto(
            i.Job.Id, i.ProfileId, i.ProfileName,
            i.Job.SourceId, i.SourceName,
            i.Job.Title, i.Job.Company, i.Job.Location, i.Job.Url, i.Job.Description,
            i.Job.PostedUtc, i.Job.FirstSeenUtc, i.Job.Processed, i.Job.Delivered)).ToList();

        return Results.Ok(new PagedResult<JobDto>(page.Total, items));
    }

    private static async Task<IResult> CountJobs(long chatId, JobService jobs, CancellationToken ct) =>
        Results.Ok(new JobCountDto(await jobs.CountStoredAsync(chatId, ct)));

    /// <summary>Marks every stored vacancy of this user as not yet sent, so the next check sends them again.</summary>
    private static async Task<IResult> Resend(long chatId, JobService jobs, CancellationToken ct) =>
        (await jobs.ResendAllAsync(chatId, ct)).ToHttp(n => Results.Ok(new ResendDto(n)));

    // ---- Checks. A check can take minutes, so POST only starts it; GET shows how it is going and how it ended.

    /// <summary>202 and a Location to poll. 409 (kind Busy) when this user's own check or the hourly check is running.</summary>
    private static IResult StartCheck(long chatId, ManualCheckTracker checks) =>
        checks.Start(chatId).ToHttp(_ =>
            Results.Accepted($"/api/{chatId}/check", checks.Get(chatId).ToDto()));

    private static IResult GetCheck(long chatId, ManualCheckTracker checks) =>
        Results.Ok(checks.Get(chatId).ToDto());

    private static IResult StopCheck(long chatId, ManualCheckTracker checks) =>
        Results.Ok(new StopCheckDto(checks.Stop(chatId)));

    // ---- Mapping

    private static ProfileDraft ToDraft(ProfileRequest r) =>
        new(r.Keyword, r.Name, r.Enabled, r.Include, r.Exclude, r.Companies, r.Locations, r.MinSalary, r.RemoteOnly);
}
