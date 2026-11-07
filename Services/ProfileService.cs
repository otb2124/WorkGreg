using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Filtering;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Services;

/// <summary>Everything needed to create or fully replace a profile. Only <see cref="Keyword"/> is required.</summary>
public sealed record ProfileDraft(
    string Keyword,
    string? Name = null,
    bool Enabled = true,
    string Include = "",
    string Exclude = "",
    string Companies = "",
    string Locations = "",
    int? MinSalary = null,
    bool RemoteOnly = false);

/// <summary>A profile together with its sources (ordered by id).</summary>
public sealed record ProfileDetails(SearchProfile Profile, IReadOnlyList<Source> Sources);

/// <summary>
/// Use cases for <see cref="SearchProfile"/>: validation, ownership checks, persistence.
/// Used by the Telegram bot and the HTTP API. Error messages are plain text.
/// Reads that cannot fail return plain values; everything that can fail returns a <see cref="Result{T, TError}"/>.
/// </summary>
public sealed class ProfileService(AppDb db)
{
    private static readonly Error ProfileNotFound = Error.NotFound("No such profile.");

    // ---- reads

    public async Task<IReadOnlyList<ProfileDetails>> ListAsync(long chatId, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.AsNoTracking()
            .Where(p => p.OwnerChatId == chatId)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.OwnerChatId == chatId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        var byProfile = sources.ToLookup(s => s.SearchProfileId);
        return profiles.Select(p => new ProfileDetails(p, byProfile[p.Id].ToList())).ToList();
    }

    public async Task<Result<ProfileDetails, Error>> GetAsync(long chatId, int id, CancellationToken ct)
    {
        var profile = await db.SearchProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct);
        if (profile is null) return ProfileNotFound;

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.SearchProfileId == id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        return new ProfileDetails(profile, sources);
    }

    // ---- create / replace / remove

    public async Task<Result<SearchProfile, Error>> CreateAsync(long chatId, ProfileDraft draft, CancellationToken ct)
    {
        var valid = Validate(draft);
        if (valid.IsFailure) return valid.Error;

        var count = await db.SearchProfiles.CountAsync(p => p.OwnerChatId == chatId, ct);
        if (count >= ProfileRules.MaxProfilesPerUser)
            return Error.Validation($"Profile limit reached ({ProfileRules.MaxProfilesPerUser}).");

        var profile = new SearchProfile { OwnerChatId = chatId };
        Apply(profile, draft, valid.Value.Name, valid.Value.Keyword);
        db.SearchProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        return profile;
    }

    /// <summary>Replaces all profile fields at once. A changed keyword is pushed to every source of the profile.</summary>
    public async Task<Result<ProfileDetails, Error>> ReplaceAsync(
        long chatId, int id, ProfileDraft draft, CancellationToken ct)
    {
        var profile = await FindOwnedAsync(chatId, id, ct);
        if (profile is null) return ProfileNotFound;

        var valid = Validate(draft);
        if (valid.IsFailure) return valid.Error;

        var sources = await db.Sources.Where(s => s.SearchProfileId == id).ToListAsync(ct);

        var keywordChanged = !string.Equals(profile.Keyword, valid.Value.Keyword, StringComparison.Ordinal);
        Apply(profile, draft, valid.Value.Name, valid.Value.Keyword);
        if (keywordChanged) ProfileRules.ApplyKeyword(profile, valid.Value.Keyword, sources);

        await db.SaveChangesAsync(ct);
        return new ProfileDetails(profile, sources.OrderBy(s => s.Id).ToList());
    }

    /// <summary>Removes the profile with its sources and stored vacancies. Returns the removed profile (for its name).</summary>
    public async Task<Result<SearchProfile, Error>> RemoveAsync(long chatId, int id, CancellationToken ct)
    {
        var profile = await FindOwnedAsync(chatId, id, ct);
        if (profile is null) return ProfileNotFound;

        var sourceIds = await db.Sources.Where(s => s.SearchProfileId == id).Select(s => s.Id).ToListAsync(ct);
        db.Jobs.RemoveRange(db.Jobs.Where(j => sourceIds.Contains(j.SourceId)));
        db.Sources.RemoveRange(db.Sources.Where(s => s.SearchProfileId == id));
        db.SearchProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);

        return profile;
    }

    // ---- single-field changes

    public Task<Result<SearchProfile, Error>> RenameAsync(long chatId, int id, string? rawName, CancellationToken ct)
    {
        var name = ProfileRules.NormalizeName(rawName);
        return name is null
            ? Task.FromResult<Result<SearchProfile, Error>>(NameError())
            : UpdateAsync(chatId, id, p => p.Name = name, ct);
    }

    /// <summary>Sets the keyword and re-derives every source URL (failures are reset because the search changed).</summary>
    public async Task<Result<SearchProfile, Error>> SetKeywordAsync(
        long chatId, int id, string? rawKeyword, CancellationToken ct)
    {
        var keyword = SourceCatalog.NormalizeKeyword(rawKeyword);
        if (keyword is null) return KeywordError();

        var profile = await FindOwnedAsync(chatId, id, ct);
        if (profile is null) return ProfileNotFound;

        var sources = await db.Sources.Where(s => s.SearchProfileId == id).ToListAsync(ct);
        ProfileRules.ApplyKeyword(profile, keyword, sources);
        await db.SaveChangesAsync(ct);

        return profile;
    }

    public Task<Result<SearchProfile, Error>> SetEnabledAsync(long chatId, int id, bool enabled, CancellationToken ct) =>
        UpdateAsync(chatId, id, p => p.Enabled = enabled, ct);

    // ---- filters (empty / null clears)

    public Task<Result<SearchProfile, Error>> SetIncludeAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        UpdateAsync(chatId, id, p => p.Include = FilterEngine.Normalize(csv), ct);

    public Task<Result<SearchProfile, Error>> SetExcludeAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        UpdateAsync(chatId, id, p => p.Exclude = FilterEngine.Normalize(csv), ct);

    public Task<Result<SearchProfile, Error>> SetCompaniesAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        UpdateAsync(chatId, id, p => p.Companies = FilterEngine.Normalize(csv), ct);

    public Task<Result<SearchProfile, Error>> SetLocationsAsync(long chatId, int id, string? csv, CancellationToken ct) =>
        UpdateAsync(chatId, id, p => p.Locations = FilterEngine.Normalize(csv), ct);

    /// <summary>Null clears the minimum salary; anything else must be positive.</summary>
    public Task<Result<SearchProfile, Error>> SetMinSalaryAsync(long chatId, int id, int? salary, CancellationToken ct) =>
        salary is <= 0
            ? Task.FromResult<Result<SearchProfile, Error>>(Error.Validation("The minimum salary must be a positive number."))
            : UpdateAsync(chatId, id, p => p.MinSalary = salary, ct);

    public Task<Result<SearchProfile, Error>> SetRemoteOnlyAsync(long chatId, int id, bool remoteOnly, CancellationToken ct) =>
        UpdateAsync(chatId, id, p => p.RemoteOnly = remoteOnly, ct);

    // ---- helpers

    private Task<SearchProfile?> FindOwnedAsync(long chatId, int id, CancellationToken ct) =>
        db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct);

    private async Task<Result<SearchProfile, Error>> UpdateAsync(
        long chatId, int id, Action<SearchProfile> change, CancellationToken ct)
    {
        var profile = await FindOwnedAsync(chatId, id, ct);
        if (profile is null) return ProfileNotFound;

        change(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    private readonly record struct Validated(string Name, string Keyword);

    private static Result<Validated, Error> Validate(ProfileDraft draft)
    {
        var keyword = SourceCatalog.NormalizeKeyword(draft.Keyword);
        if (keyword is null) return KeywordError();

        var name = string.IsNullOrWhiteSpace(draft.Name)
            ? ProfileRules.NameFromKeyword(keyword)
            : ProfileRules.NormalizeName(draft.Name);
        if (name is null) return NameError();

        return new Validated(name, keyword);
    }

    private static void Apply(SearchProfile p, ProfileDraft draft, string name, string keyword)
    {
        p.Name = name;
        p.Keyword = keyword;
        p.Enabled = draft.Enabled;
        p.Include = FilterEngine.Normalize(draft.Include);
        p.Exclude = FilterEngine.Normalize(draft.Exclude);
        p.Companies = FilterEngine.Normalize(draft.Companies);
        p.Locations = FilterEngine.Normalize(draft.Locations);
        p.MinSalary = draft.MinSalary is > 0 ? draft.MinSalary : null;
        p.RemoteOnly = draft.RemoteOnly;
    }

    private static Error KeywordError() =>
        Error.Validation($"The keyword must be 1-{SourceCatalog.MaxKeywordLength} characters.");

    private static Error NameError() =>
        Error.Validation($"The name must be 1-{ProfileRules.MaxNameLength} characters.");
}