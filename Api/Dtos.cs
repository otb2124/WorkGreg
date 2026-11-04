using WorkGreg.Domain;

namespace WorkGreg.Api;

public sealed record SourceDto(
    int Id,
    FetchType Type,
    string? Provider,
    string? Keyword,
    string Name,
    string Url,
    bool Enabled,
    bool Baselined,
    int ConsecutiveFailures,
    string? LastError,
    DateTime? LastSuccessUtc);

/// <summary>A search = a hardcoded source (see GET /api/providers) + a keyword.</summary>
public sealed record AddSourceRequest(string Provider, string Keyword);

/// <summary>Null fields are left unchanged.</summary>
public sealed record UpdateSourceRequest(string? Keyword, bool? Enabled);

public sealed record ProviderDto(string Id, string Name, FetchType Type, bool IsAvailable, IReadOnlyList<string> KeywordExamples);

public sealed record FilterDto(
    string Include,
    string Exclude,
    string Companies,
    string Locations,
    int? MinSalary,
    bool RemoteOnly,
    bool Paused,
    DeliveryMode Delivery,
    int DigestHourUtc);

public sealed record JobDto(
    int Id,
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

public sealed record SummaryDto(int Sources, int EnabledSources, int FailingSources, int PendingJobs, int DeliveredJobs, bool Paused);

public sealed record CheckResult(int Sent);