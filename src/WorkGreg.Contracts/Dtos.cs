using WorkGreg.Domain;

namespace WorkGreg.Contracts;

public sealed record SourceDto(
    int Id,
    int ProfileId,
    FetchType Type,
    string? Provider,
    string Name,
    string Url,
    bool Enabled,
    int ConsecutiveFailures,
    string? LastError,
    DateTime? LastSuccessUtc);

/// <summary>Attaches a hardcoded source (see GET /api/providers) to a profile. The keyword comes from the profile.</summary>
public sealed record AddSourceRequest(string Provider);

/// <summary>Null fields are left unchanged.</summary>
public sealed record UpdateSourceRequest(bool? Enabled);

public sealed record ProviderDto(string Id, string Name, FetchType Type, bool IsAvailable, IReadOnlyList<string> KeywordExamples, IReadOnlyList<string> Regions);

public sealed record RegionDto(string Id, string Label);

/// <summary>Create (POST) and replace (PUT) body for a profile; the UI sends the whole form. Name defaults to the keyword.</summary>
public sealed record ProfileRequest(
    string? Name,
    string Keyword,
    bool Enabled,
    IReadOnlyList<string>? Include,
    IReadOnlyList<string>? Exclude,
    IReadOnlyList<string>? Companies,
    IReadOnlyList<string>? Locations,
    int? MinSalary,
    bool RemoteOnly);

public sealed record ProfileDto(
    int Id,
    string Name,
    string Keyword,
    bool Enabled,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<string> Companies,
    IReadOnlyList<string> Locations,
    int? MinSalary,
    bool RemoteOnly,
    IReadOnlyList<SourceDto> Sources);

/// <summary>A source was attached to or removed from a profile.</summary>
public sealed record SourceChangeDto(int ProfileId, string ProfileName, SourceDto Source, bool Added);

/// <summary>User-level settings shared by all profiles.</summary>
public sealed record SettingsDto(bool Paused, DeliveryMode Delivery, int DigestHourUtc);

public sealed record JobDto(
    int Id,
    int ProfileId,
    string ProfileName,
    int SourceId,
    string SourceName,
    string Title,
    string? Company,
    string? Location,
    string Url,
    string? Description,
    DateTime? PostedUtc,
    DateTime FirstSeenUtc,
    bool Processed,
    bool Delivered);

public sealed record PagedResult<T>(int Total, IReadOnlyList<T> Items);

public sealed record FetchTypeDto(FetchType Type, bool IsAvailable);

public sealed record SummaryDto(
    int Profiles, int EnabledProfiles, int Sources, int EnabledSources, int FailingSources,
    int PendingJobs, int DeliveredJobs, bool Paused);

public sealed record CheckResult(int Sent);