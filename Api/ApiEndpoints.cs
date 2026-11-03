using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;
using WorkGreg.Services;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Api;

public static class ApiEndpoints
{
    private const int MaxSourcesPerUser = 20;
    private const int MaxPageSize = 200;

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();

        api.MapGet("/fetch-types", GetFetchTypes);

        var user = api.MapGroup("/{chatId:long}").AddEndpointFilter<ChatAccessFilter>();

        user.MapGet("/summary", GetSummary);

        user.MapGet("/sources", ListSources);
        user.MapPost("/sources", AddSource);
        user.MapPatch("/sources/{id:int}", UpdateSource);
        user.MapDelete("/sources/{id:int}", RemoveSource);

        user.MapGet("/filters", GetFilters);
        user.MapPut("/filters", SetFilters);

        user.MapGet("/jobs", ListJobs);
        user.MapPost("/check", CheckNow);
    }

    private static IResult GetFetchTypes(FetcherRegistry fetchers) =>
        Results.Ok(fetchers.All.Select(f => new FetchTypeDto(f.Type, f.IsAvailable)));

    private static async Task<IResult> GetSummary(long chatId, AppDb db, CancellationToken ct)
    {
        var sources = await db.Sources.AsNoTracking().Where(s => s.OwnerChatId == chatId).ToListAsync(ct);
        var pending = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId && !j.Processed, ct);
        var delivered = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId && j.Delivered, ct);
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.ChatId == chatId, ct);

        return Results.Ok(new SummaryDto(
            sources.Count,
            sources.Count(s => s.Enabled),
            sources.Count(s => s.ConsecutiveFailures > 0),
            pending,
            delivered,
            profile?.Paused ?? false));
    }

    // ---- Sources ----

    private static async Task<IResult> ListSources(long chatId, AppDb db, CancellationToken ct)
    {
        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.OwnerChatId == chatId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        return Results.Ok(sources.Select(ToDto));
    }

    private static async Task<IResult> AddSource(
        long chatId, AddSourceRequest req, AppDb db, FetcherRegistry fetchers, CancellationToken ct)
    {
        if (!fetchers.Get(req.Type).IsAvailable)
            return Results.BadRequest($"Type '{req.Type}' is planned but not implemented yet.");

        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Results.BadRequest("That doesn't look like a valid http(s) URL.");

        if (await db.Sources.CountAsync(s => s.OwnerChatId == chatId, ct) >= MaxSourcesPerUser)
            return Results.BadRequest($"Source limit reached ({MaxSourcesPerUser}).");

        if (await db.Sources.AnyAsync(s => s.OwnerChatId == chatId && s.Url == uri.ToString(), ct))
            return Results.Conflict("You already have that source.");

        var source = new Source
        {
            OwnerChatId = chatId,
            Type = req.Type,
            Url = uri.ToString(),
            Name = string.IsNullOrWhiteSpace(req.Name) ? uri.Host : req.Name.Trim()
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/{chatId}/sources/{source.Id}", ToDto(source));
    }

    private static async Task<IResult> UpdateSource(
        long chatId, int id, UpdateSourceRequest req, AppDb db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.OwnerChatId == chatId, ct);
        if (source is null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(req.Name)) source.Name = req.Name.Trim();
        if (req.Enabled is { } enabled) source.Enabled = enabled;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(source));
    }

    private static async Task<IResult> RemoveSource(long chatId, int id, AppDb db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.OwnerChatId == chatId, ct);
        if (source is null) return Results.NotFound();

        db.Jobs.RemoveRange(db.Jobs.Where(j => j.SourceId == id));
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Filters ----

    private static async Task<IResult> GetFilters(long chatId, AppDb db, CancellationToken ct)
    {
        var p = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.ChatId == chatId, ct)
                ?? new UserProfile { ChatId = chatId };
        return Results.Ok(ToDto(p));
    }

    /// <summary>Replaces all filter fields at once (the UI sends the whole form).</summary>
    private static async Task<IResult> SetFilters(long chatId, FilterDto req, AppDb db, CancellationToken ct)
    {
        var p = await db.Profiles.FindAsync(new object[] { chatId }, ct);
        if (p is null)
        {
            p = new UserProfile { ChatId = chatId };
            db.Profiles.Add(p);
        }

        p.Include = FilterEngine.Normalize(req.Include);
        p.Exclude = FilterEngine.Normalize(req.Exclude);
        p.Companies = FilterEngine.Normalize(req.Companies);
        p.Locations = FilterEngine.Normalize(req.Locations);
        p.MinSalary = req.MinSalary is > 0 ? req.MinSalary : null;
        p.RemoteOnly = req.RemoteOnly;
        p.Paused = req.Paused;
        p.Delivery = req.Delivery;
        p.DigestHourUtc = Math.Clamp(req.DigestHourUtc, 0, 23);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(p));
    }

    // ---- Jobs ----

    private static async Task<IResult> ListJobs(
        long chatId, AppDb db, CancellationToken ct,
        int? sourceId = null, bool? delivered = null, string? q = null, int skip = 0, int take = 50)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, MaxPageSize);

        var query = db.Jobs.AsNoTracking().Where(j => j.OwnerChatId == chatId);
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

        var names = await db.Sources.AsNoTracking()
            .Where(s => s.OwnerChatId == chatId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var items = jobs.Select(j => new JobDto(
            j.Id, j.SourceId, names.GetValueOrDefault(j.SourceId, "?"),
            j.Title, j.Company, j.Location, j.Url, j.Description,
            j.PostedUtc, j.FirstSeenUtc, j.Processed, j.Delivered)).ToList();

        return Results.Ok(new PagedResult<JobDto>(total, items));
    }

    private static async Task<IResult> CheckNow(long chatId, PollRunner runner, CancellationToken ct)
    {
        var sent = await runner.RunAsync(chatId, ct);
        return Results.Ok(new CheckResult(sent));
    }

    // ---- Mapping ----

    private static SourceDto ToDto(Source s) =>
        new(s.Id, s.Type, s.Name, s.Url, s.Enabled, s.Baselined, s.ConsecutiveFailures, s.LastError, s.LastSuccessUtc);

    private static FilterDto ToDto(UserProfile p) =>
        new(p.Include, p.Exclude, p.Companies, p.Locations, p.MinSalary,
            p.RemoteOnly, p.Paused, p.Delivery, p.DigestHourUtc);
}