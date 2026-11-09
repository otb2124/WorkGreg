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

        api.MapGet("/fetch-types", GetFetchTypes);
        api.MapGet("/providers", GetProviders);

        var user = api.MapGroup("/{chatId:long}").AddEndpointFilter<ChatAccessFilter>();

        user.MapGet("/summary", GetSummary);

        user.MapGet("/settings", GetSettings);
        user.MapPut("/settings", SetSettings);

        user.MapGet("/profiles", ListProfiles);
        user.MapPost("/profiles", AddProfile);
        user.MapGet("/profiles/{id:int}", GetProfile);
        user.MapPut("/profiles/{id:int}", UpdateProfile);
        user.MapDelete("/profiles/{id:int}", RemoveProfile);

        user.MapPost("/profiles/{id:int}/sources", AddSource);
        user.MapPatch("/profiles/{id:int}/sources/{sourceId:int}", UpdateSource);
        user.MapDelete("/profiles/{id:int}/sources/{sourceId:int}", RemoveSource);

        user.MapGet("/jobs", ListJobs);
        user.MapPost("/check", CheckNow);
    }

    // ---- Catalog

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

    private static async Task<IResult> GetSettings(long chatId, SettingsService settings, CancellationToken ct) =>
        Results.Ok((await settings.GetAsync(chatId, ct)).ToDto());

    private static async Task<IResult> SetSettings(
        long chatId, SettingsDto req, SettingsService settings, CancellationToken ct) =>
        (await settings.ReplaceAsync(chatId, req.Paused, req.Delivery, req.DigestHourUtc, ct))
            .ToHttp(s => Results.Ok(s.ToDto()));

    // ---- Profiles

    private static async Task<IResult> ListProfiles(long chatId, ProfileService profiles, CancellationToken ct) =>
        Results.Ok((await profiles.ListAsync(chatId, ct)).Select(d => d.ToDto()));

    private static async Task<IResult> GetProfile(long chatId, int id, ProfileService profiles, CancellationToken ct) =>
        (await profiles.GetAsync(chatId, id, ct)).ToHttp(d => Results.Ok(d.ToDto()));

    private static async Task<IResult> AddProfile(
        long chatId, ProfileRequest req, ProfileService profiles, CancellationToken ct) =>
        (await profiles.CreateAsync(chatId, ToDraft(req), ct))
            .ToHttp(p => Results.Created($"/api/{chatId}/profiles/{p.Id}", p.ToDto([])));

    /// <summary>Replaces all profile fields at once. A changed keyword is pushed to every source of the profile.</summary>
    private static async Task<IResult> UpdateProfile(
        long chatId, int id, ProfileRequest req, ProfileService profiles, CancellationToken ct) =>
        (await profiles.ReplaceAsync(chatId, id, ToDraft(req), ct)).ToHttp(d => Results.Ok(d.ToDto()));

    private static async Task<IResult> RemoveProfile(long chatId, int id, ProfileService profiles, CancellationToken ct) =>
        (await profiles.RemoveAsync(chatId, id, ct)).ToHttp(_ => Results.NoContent());

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

    private static async Task<IResult> CheckNow(long chatId, JobService jobs, CancellationToken ct) =>
        (await jobs.CheckNowAsync(chatId, ct)).ToHttp(r => Results.Ok(new CheckResult(r.Sent)));

    // ---- Mapping

    private static ProfileDraft ToDraft(ProfileRequest r) =>
        new(r.Keyword, r.Name, r.Enabled, r.Include, r.Exclude, r.Companies, r.Locations, r.MinSalary, r.RemoteOnly);
}