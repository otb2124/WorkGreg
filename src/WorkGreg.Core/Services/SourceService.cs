using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Services;

/// <summary>What happened to a profile's source list. <see cref="Added"/> is false when the source was removed.</summary>
public sealed record SourceChange(SearchProfile Profile, Source Source, bool Added);

/// <summary>A catalog provider and whether its fetcher is implemented.</summary>
public sealed record ProviderInfo(SourceProvider Provider, bool IsAvailable);

public sealed record FetchTypeInfo(FetchType Type, bool IsAvailable);

/// <summary>
/// Use cases for the sources (job boards) attached to a profile.
/// Used by the Telegram bot and the HTTP API. Failures are <see cref="Errors"/> (a stable code plus a plain English message).
///
/// Provider ids: <see cref="Source.Provider"/> is always stored in the catalog's form (lower case, see
/// <see cref="ProfileRules.NewSource"/>), and every id typed by a user goes through
/// <see cref="ProfileRules.NormalizeProviderId"/> before it is compared, so add, remove and toggle all agree
/// and the queries compare with a plain "=" (which can use an index).
/// </summary>
public sealed class SourceService(AppDb db, FetcherRegistry fetchers)
{
    // ---- catalog (not per user)

    public IReadOnlyList<FetchTypeInfo> FetchTypes() =>
        fetchers.All.Select(f => new FetchTypeInfo(f.Type, f.IsAvailable)).ToList();

    public IReadOnlyList<ProviderInfo> Providers() =>
        SourceCatalog.All.Select(p => new ProviderInfo(p, fetchers.Get(p.Type).IsAvailable)).ToList();

    /// <summary>Only providers that can actually be fetched right now.</summary>
    public IReadOnlyList<SourceProvider> AvailableProviders() =>
        Providers().Where(p => p.IsAvailable).Select(p => p.Provider).ToList();

    // ---- add

    public async Task<Result<SourceChange, Error>> AddAsync(
        long chatId, int profileId, string? providerId, CancellationToken ct)
    {
        var profile = await FindProfileAsync(chatId, profileId, ct);
        if (profile is null) return Errors.ProfileNotFound;

        var provider = SourceCatalog.Find(ProfileRules.NormalizeProviderId(providerId));
        if (provider is null)
            return Errors.UnknownSource(
                (providerId ?? "").Trim(),
                string.Join(", ", SourceCatalog.All.Select(p => p.Id)));

        if (!fetchers.Get(provider.Type).IsAvailable)
            return Errors.SourceNotImplemented(provider.Name);

        var stored = provider.Id.ToLowerInvariant();
        var exists = await db.Sources.AnyAsync(s => s.SearchProfileId == profileId && s.Provider == stored, ct);
        if (exists) return Errors.SourceAlreadyAdded(provider.Name);

        var source = ProfileRules.NewSource(profile, provider);
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        return new SourceChange(profile, source, Added: true);
    }

    // ---- remove

    /// <summary>Removes by source row id (the HTTP API addresses sources like this).</summary>
    public async Task<Result<SourceChange, Error>> RemoveAsync(
        long chatId, int profileId, int sourceId, CancellationToken ct)
    {
        var profile = await FindProfileAsync(chatId, profileId, ct);
        if (profile is null) return Errors.ProfileNotFound;

        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.Id == sourceId && s.SearchProfileId == profileId, ct);
        if (source is null) return Errors.SourceNotInProfile;

        return await RemoveCoreAsync(profile, source, ct);
    }

    /// <summary>Removes by provider id, whatever case the caller typed (the bot addresses sources like this).</summary>
    public async Task<Result<SourceChange, Error>> RemoveByProviderAsync(
        long chatId, int profileId, string? providerId, CancellationToken ct)
    {
        var profile = await FindProfileAsync(chatId, profileId, ct);
        if (profile is null) return Errors.ProfileNotFound;

        var wanted = ProfileRules.NormalizeProviderId(providerId);
        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.SearchProfileId == profileId && s.Provider == wanted, ct);
        if (source is null) return Errors.SourceNotInProfile;

        return await RemoveCoreAsync(profile, source, ct);
    }

    // ---- toggle / update

    /// <summary>Adds the provider if the profile lacks it, removes it otherwise (the menu's checkbox).</summary>
    public async Task<Result<SourceChange, Error>> ToggleAsync(
        long chatId, int profileId, string providerId, CancellationToken ct)
    {
        var wanted = ProfileRules.NormalizeProviderId(providerId);

        var attached = await db.Sources.AnyAsync(s => s.SearchProfileId == profileId && s.Provider == wanted, ct);
        return attached
            ? await RemoveByProviderAsync(chatId, profileId, wanted, ct)
            : await AddAsync(chatId, profileId, wanted, ct);
    }

    /// <summary>Null fields are left unchanged.</summary>
    public async Task<Result<Source, Error>> UpdateAsync(
        long chatId, int profileId, int sourceId, bool? enabled, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.Id == sourceId && s.SearchProfileId == profileId && s.SearchProfile.OwnerChatId == chatId, ct);
        if (source is null) return Errors.SourceNotInProfile;

        if (enabled is { } value) source.Enabled = value;

        await db.SaveChangesAsync(ct);
        return source;
    }

    // ---- helpers

    private Task<SearchProfile?> FindProfileAsync(long chatId, int profileId, CancellationToken ct) =>
        db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == profileId && p.OwnerChatId == chatId, ct);

    private async Task<Result<SourceChange, Error>> RemoveCoreAsync(SearchProfile profile, Source source, CancellationToken ct)
    {
        db.Sources.Remove(source);   // the database cascades to the source's stored vacancies
        await db.SaveChangesAsync(ct);

        return new SourceChange(profile, source, Added: false);
    }
}