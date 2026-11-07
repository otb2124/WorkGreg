using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;
using WorkGreg.Services;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Api;

public static class ApiEndpoints
{
    private const int MaxPageSize = 200;

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

    private static IResult GetFetchTypes(FetcherRegistry fetchers) =>
        Results.Ok(fetchers.All.Select(f => new FetchTypeDto(f.Type, f.IsAvailable)));

    private static IResult GetProviders(FetcherRegistry fetchers) =>
        Results.Ok(SourceCatalog.All.Select(p => new ProviderDto(
            p.Id, p.Name, p.Type, fetchers.Get(p.Type).IsAvailable, p.KeywordExamples, SourceCatalog.RegionsOf(p))));

    private static async Task<IResult> GetSummary(long chatId, AppDb db, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.AsNoTracking().Where(p => p.OwnerChatId == chatId).ToListAsync(ct);
        var sources = await db.Sources.AsNoTracking().Where(s => s.OwnerChatId == chatId).ToListAsync(ct);
        var pending = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId && !j.Processed, ct);
        var delivered = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId && j.Delivered, ct);
        var settings = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.ChatId == chatId, ct);

        return Results.Ok(new SummaryDto(
            profiles.Count,
            profiles.Count(p => p.Enabled),
            sources.Count,
            sources.Count(s => s.Enabled),
            sources.Count(s => s.ConsecutiveFailures > 0),
            pending,
            delivered,
            settings?.Paused ?? false));
    }

    // ---- Settings (user level) ----

    private static async Task<IResult> GetSettings(long chatId, AppDb db, CancellationToken ct)
    {
        var s = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.ChatId == chatId, ct)
                ?? new UserProfile { ChatId = chatId };
        return Results.Ok(ToDto(s));
    }

    private static async Task<IResult> SetSettings(long chatId, SettingsDto req, AppDb db, CancellationToken ct)
    {
        var s = await db.Profiles.FindAsync(new object[] { chatId }, ct);
        if (s is null)
        {
            s = new UserProfile { ChatId = chatId };
            db.Profiles.Add(s);
        }

        s.Paused = req.Paused;
        s.Delivery = req.Delivery;
        s.DigestHourUtc = Math.Clamp(req.DigestHourUtc, 0, 23);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(s));
    }

    // ---- Profiles ----

    private static async Task<IResult> ListProfiles(long chatId, AppDb db, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.AsNoTracking()
            .Where(p => p.OwnerChatId == chatId)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.OwnerChatId == chatId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        return Results.Ok(profiles.Select(p => ToDto(p, sources.Where(s => s.SearchProfileId == p.Id))));
    }

    private static async Task<IResult> GetProfile(long chatId, int id, AppDb db, CancellationToken ct)
    {
        var p = await db.SearchProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerChatId == chatId, ct);
        if (p is null) return Results.NotFound();

        var sources = await db.Sources.AsNoTracking().Where(s => s.SearchProfileId == id).OrderBy(s => s.Id).ToListAsync(ct);
        return Results.Ok(ToDto(p, sources));
    }

    private static async Task<IResult> AddProfile(long chatId, ProfileRequest req, AppDb db, CancellationToken ct)
    {
        var keyword = SourceCatalog.NormalizeKeyword(req.Keyword);
        if (keyword is null)
            return Results.BadRequest($"The keyword must be 1-{SourceCatalog.MaxKeywordLength} characters.");

        var name = string.IsNullOrWhiteSpace(req.Name)
            ? ProfileRules.NameFromKeyword(keyword)
            : ProfileRules.NormalizeName(req.Name);
        if (name is null)
            return Results.BadRequest($"The name must be 1-{ProfileRules.MaxNameLength} characters.");

        var count = await db.SearchProfiles.CountAsync(p => p.OwnerChatId == chatId, ct);
        if (count >= ProfileRules.MaxProfilesPerUser)
            return Results.BadRequest($"Profile limit reached ({ProfileRules.MaxProfilesPerUser}).");

        var profile = new SearchProfile { OwnerChatId = chatId };
        Apply(profile, req, name, keyword);
        db.SearchProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/{chatId}/profiles/{profile.Id}", ToDto(profile, []));
    }

    /// <summary>Replaces all profile fields at once. A changed keyword is pushed to every source of the profile.</summary>
    private static async Task<IResult> UpdateProfile(long chatId, int id, ProfileRequest req, AppDb db, CancellationToken ct)
    {
        var profile = await db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct);
        if (profile is null) return Results.NotFound();

        var keyword = SourceCatalog.NormalizeKeyword(req.Keyword);
        if (keyword is null)
            return Results.BadRequest($"The keyword must be 1-{SourceCatalog.MaxKeywordLength} characters.");

        var name = string.IsNullOrWhiteSpace(req.Name)
            ? ProfileRules.NameFromKeyword(keyword)
            : ProfileRules.NormalizeName(req.Name);
        if (name is null)
            return Results.BadRequest($"The name must be 1-{ProfileRules.MaxNameLength} characters.");

        var sources = await db.Sources.Where(s => s.SearchProfileId == id).ToListAsync(ct);

        var keywordChanged = !string.Equals(profile.Keyword, keyword, StringComparison.Ordinal);
        Apply(profile, req, name, keyword);
        if (keywordChanged) ProfileRules.ApplyKeyword(profile, keyword, sources);

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(profile, sources.OrderBy(s => s.Id)));
    }

    private static async Task<IResult> RemoveProfile(long chatId, int id, AppDb db, CancellationToken ct)
    {
        var profile = await db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct);
        if (profile is null) return Results.NotFound();

        var sourceIds = await db.Sources.Where(s => s.SearchProfileId == id).Select(s => s.Id).ToListAsync(ct);
        db.Jobs.RemoveRange(db.Jobs.Where(j => sourceIds.Contains(j.SourceId)));
        db.Sources.RemoveRange(db.Sources.Where(s => s.SearchProfileId == id));
        db.SearchProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Sources of a profile ----

    private static async Task<IResult> AddSource(
        long chatId, int id, AddSourceRequest req, AppDb db, FetcherRegistry fetchers, CancellationToken ct)
    {
        var profile = await db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct);
        if (profile is null) return Results.NotFound();

        var provider = SourceCatalog.Find(req.Provider);
        if (provider is null)
            return Results.BadRequest(
                $"Unknown source '{req.Provider}'. Available: {string.Join(", ", SourceCatalog.All.Select(p => p.Id))}.");

        if (!fetchers.Get(provider.Type).IsAvailable)
            return Results.BadRequest($"{provider.Name} is planned but not implemented yet.");

        if (await db.Sources.AnyAsync(s => s.SearchProfileId == id && s.Provider == provider.Id, ct))
            return Results.Conflict("This profile already uses that source.");

        var source = ProfileRules.NewSource(profile, provider);
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/{chatId}/profiles/{id}/sources/{source.Id}", ToDto(source));
    }

    private static async Task<IResult> UpdateSource(
        long chatId, int id, int sourceId, UpdateSourceRequest req, AppDb db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.Id == sourceId && s.SearchProfileId == id && s.OwnerChatId == chatId, ct);
        if (source is null) return Results.NotFound();

        if (req.Enabled is { } enabled) source.Enabled = enabled;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(source));
    }

    private static async Task<IResult> RemoveSource(long chatId, int id, int sourceId, AppDb db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.Id == sourceId && s.SearchProfileId == id && s.OwnerChatId == chatId, ct);
        if (source is null) return Results.NotFound();

        db.Jobs.RemoveRange(db.Jobs.Where(j => j.SourceId == sourceId));
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Jobs ----

    private static async Task<IResult> ListJobs(
        long chatId, AppDb db, CancellationToken ct,
        int? profileId = null, int? sourceId = null, bool? delivered = null, string? q = null, int skip = 0, int take = 50)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, MaxPageSize);

        var query = db.Jobs.AsNoTracking().Where(j => j.OwnerChatId == chatId);
        if (profileId is { } pid)
        {
            var ids = await db.Sources.Where(s => s.OwnerChatId == chatId && s.SearchProfileId == pid)
                .Select(s => s.Id).ToListAsync(ct);
            query = query.Where(j => ids.Contains(j.SourceId));
        }
        if (sourceId is { } sid) query = query.Where(j => j.SourceId == sid);
        if (delivered is { } d) query = query.Where(j => j.Delivered == d);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(j => j.Title.Contains(term) || (j.Company != null && j.Company.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var jobs = await query
            .OrderByDescending(j => j.PostedUtc ?? j.FirstSeenUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.OwnerChatId == chatId)
            .ToDictionaryAsync(s => s.Id, ct);
        var profileNames = await db.SearchProfiles.AsNoTracking()
            .Where(p => p.OwnerChatId == chatId)
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var items = jobs.Select(j =>
        {
            sources.TryGetValue(j.SourceId, out var src);
            var pId = src?.SearchProfileId ?? 0;
            return new JobDto(
                j.Id, pId, profileNames.GetValueOrDefault(pId, "?"),
                j.SourceId, src?.Name ?? "?",
                j.Title, j.Company, j.Location, j.Url, j.Description,
                j.PostedUtc, j.FirstSeenUtc, j.Processed, j.Delivered);
        }).ToList();

        return Results.Ok(new PagedResult<JobDto>(total, items));
    }

    private static async Task<IResult> CheckNow(long chatId, PollRunner runner, CancellationToken ct)
    {
        var result = await runner.RunAsync(chatId, ct);
        return Results.Ok(new CheckResult(result.Sent));
    }

    // ---- Mapping ----

    private static void Apply(SearchProfile p, ProfileRequest req, string name, string keyword)
    {
        p.Name = name;
        p.Keyword = keyword;
        p.Enabled = req.Enabled;
        p.Include = FilterEngine.Normalize(req.Include);
        p.Exclude = FilterEngine.Normalize(req.Exclude);
        p.Companies = FilterEngine.Normalize(req.Companies);
        p.Locations = FilterEngine.Normalize(req.Locations);
        p.MinSalary = req.MinSalary is > 0 ? req.MinSalary : null;
        p.RemoteOnly = req.RemoteOnly;
    }

    private static SourceDto ToDto(Source s) =>
        new(s.Id, s.SearchProfileId, s.Type, s.Provider, s.Name, s.Url, s.Enabled, s.Baselined,
            s.ConsecutiveFailures, s.LastError, s.LastSuccessUtc);

    private static ProfileDto ToDto(SearchProfile p, IEnumerable<Source> sources) =>
        new(p.Id, p.Name, p.Keyword, p.Enabled, p.Include, p.Exclude, p.Companies, p.Locations,
            p.MinSalary, p.RemoteOnly, sources.Select(s => ToDto(s)).ToList());

    private static SettingsDto ToDto(UserProfile s) => new(s.Paused, s.Delivery, s.DigestHourUtc);
}
