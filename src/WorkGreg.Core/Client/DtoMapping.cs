using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Client;

/// <summary>Entities and service results to contract DTOs. Used by the in-process client and by the HTTP endpoints, so both say the same thing.</summary>
public static class DtoMapping
{
    public static SourceDto ToDto(this Source s) =>
        new(s.Id, s.SearchProfileId, s.Type, s.Provider, s.Name, s.Url, s.Enabled,
            s.ConsecutiveFailures, s.LastError, s.LastSuccessUtc);

    public static ProfileDto ToDto(this SearchProfile p, IEnumerable<Source> sources) =>
        new(p.Id, p.Name, p.Keyword, p.Enabled,
            p.Include.ToList(), p.Exclude.ToList(), p.Companies.ToList(), p.Locations.ToList(),
            p.MinSalary, p.RemoteOnly,
            sources.Select(s => s.ToDto()).ToList());

    public static ProfileDto ToDto(this ProfileDetails d) => d.Profile.ToDto(d.Sources);

    public static SettingsDto ToDto(this UserSettings s) => new(s.Paused, s.Delivery, s.DigestHourUtc);

    public static SourceChangeDto ToDto(this SourceChange c) =>
        new(c.Profile.Id, c.Profile.Name, c.Source.ToDto(), c.Added);

    public static ProviderDto ToDto(this ProviderInfo i) =>
        new(i.Provider.Id, i.Provider.Name, i.Provider.Type, i.IsAvailable,
            i.Provider.KeywordExamples, SourceCatalog.RegionsOf(i.Provider));
}