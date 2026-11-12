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

    /// <param name="autofillReady">Whether the profile has first name, last name and email stored for autofill (see AutofillService.ReadyProfileIdsAsync).</param>
    public static ProfileDto ToDto(this SearchProfile p, IEnumerable<Source> sources, bool autofillReady = false) =>
        new(p.Id, p.Name, p.Keyword, p.Enabled,
            p.Include.ToList(), p.Exclude.ToList(), p.Companies.ToList(), p.Locations.ToList(),
            p.MinSalary, p.RemoteOnly,
            sources.Select(s => s.ToDto()).ToList(),
            autofillReady);

    public static ProfileDto ToDto(this ProfileDetails d, bool autofillReady = false) =>
        d.Profile.ToDto(d.Sources, autofillReady);

    public static AutofillDocumentDto ToDto(this AutofillDocumentInfo d) => new(d.Kind, d.FileName, d.ContentType, d.SizeBytes);

    public static AutofillSettingsDto ToDto(this AutofillSettings s) =>
        new(s.ProfileId,
            s.StandardFields.Where(f => f.Key is not null).Select(f => new AutofillValueDto(f.Key!.Value, f.Value)).ToList(),
            s.CustomRules.Select(r => new AutofillRuleDto(r.Id, r.MatchBy, r.Pattern ?? "", r.IsRegex, r.Value, r.SortOrder)).ToList(),
            s.Documents.Select(d => d.ToDto()).ToList());

    public static ApplicationDto ToDto(this Application a) =>
        new(a.Id, a.SearchProfileId, a.Title, a.Company, a.Url, a.Ats, a.Status, a.Error,
            a.CreatedUtc, a.UpdatedUtc, a.SubmittedUtc);

    public static AutofillPreviewDto ToDto(this AutofillPreview p) =>
        new(p.DeliveryId, p.ProfileId, p.ProfileName, p.Title, p.Company, p.Ready,
            p.Values.Select(f => new AutofillValueDto(f.Key!.Value, f.Value)).ToList(),
            p.Missing.ToList(), p.HasCv, p.Existing?.ToDto());

    public static SettingsDto ToDto(this UserSettings s) => new(s.Paused, s.Delivery, s.DigestHourUtc);

    public static SourceChangeDto ToDto(this SourceChange c) =>
        new(c.Profile.Id, c.Profile.Name, c.Source.ToDto(), c.Added);

    public static ProviderDto ToDto(this ProviderInfo i) =>
        new(i.Provider.Id, i.Provider.Name, i.Provider.Type, i.IsAvailable,
            i.Provider.KeywordExamples, SourceCatalog.RegionsOf(i.Provider));

    public static CheckOutcomeDto ToDto(this CheckOutcome o) => new(o.Sent, o.Stopped, o.Failed, o.FinishedUtc);

    public static CheckStatusDto ToDto(this CheckState s) => new(s.Status, s.Queued, s.Last?.ToDto());
}
