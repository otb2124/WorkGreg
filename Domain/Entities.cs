namespace WorkGreg.Domain;

/// <summary>How a source is fetched. Add new values here, then add an IJobFetcher for them.</summary>
public enum FetchType
{
    Feed = 0,     // RSS / Atom
    Html = 1,     // plain HTTP + HTML/JSON parsing (planned)
    Browser = 2,  // headless browser (planned)
    Api = 3       // JSON endpoint; config lives in SourceProvider.Api
}

/// <summary>
/// One site that a <see cref="SearchProfile"/> reads from. The search keyword is NOT stored here:
/// it belongs to the profile, so changing it once updates every source of that profile.
/// </summary>
public class Source
{
    public int Id { get; set; }
    public long OwnerChatId { get; set; }

    /// <summary>The profile this source belongs to. A site used by two profiles is two Source rows.</summary>
    public int SearchProfileId { get; set; }

    /// <summary>Display name, the provider's name (e.g. "Remotive").</summary>
    public string Name { get; set; } = "";
    public FetchType Type { get; set; }

    /// <summary>Derived from Provider + the profile's keyword. Kept in sync when the keyword changes.</summary>
    public string Url { get; set; } = "";

    /// <summary>Catalog id of the source (see <see cref="SourceCatalog"/>), e.g. "remotive". Null for legacy rows added with a raw URL.</summary>
    public string? Provider { get; set; }

    /// <summary>Fetcher-specific settings (e.g. CSS selectors for Html). Unused by Feed.</summary>
    public string? ConfigJson { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>False until the first successful fetch.</summary>
    public bool Baselined { get; set; }

    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
}

public class Job
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public long OwnerChatId { get; set; }
    public string ExternalId { get; set; } = "";

    /// <summary>Normalized company+title hash, used to skip the same vacancy appearing on several boards (or in several profiles).</summary>
    public string Hash { get; set; } = "";

    public string Title { get; set; } = "";
    public string? Company { get; set; }
    public string? Location { get; set; }
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? PostedUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public int? Salary { get; set; }            // in Job, for MinSalary filter
    public string? EmploymentType { get; set; } // full-time / contract

    /// <summary>Filters were evaluated for this job (matched or not).</summary>
    public bool Processed { get; set; }

    /// <summary>A Telegram message was sent for this job.</summary>
    public bool Delivered { get; set; }
}

/// <summary>
/// One job hunt: a keyword, a set of filters and a set of sources (<see cref="Source"/> rows pointing here).
/// A user can have several, e.g. "Full stack" and "IT support", each with its own filters and sources.
/// </summary>
public class SearchProfile
{
    public int Id { get; set; }
    public long OwnerChatId { get; set; }

    /// <summary>Label shown in menus and messages. Defaults to the keyword.</summary>
    public string Name { get; set; } = "";

    /// <summary>The search keyword used by every source of this profile. Also enforced locally on every fetched job.</summary>
    public string Keyword { get; set; } = "";

    public string Include { get; set; } = "";
    public string Exclude { get; set; } = "";
    /// <summary>Only deliver jobs whose Company is in this comma list (empty = any).</summary>
    public string Companies { get; set; } = "";
    /// <summary>Only deliver jobs whose Location is in this comma list (empty = any).</summary>
    public string Locations { get; set; } = "";
    /// <summary>Min salary if the fetcher exposes it (null = no constraint).</summary>
    public int? MinSalary { get; set; }
    public bool RemoteOnly { get; set; }

    /// <summary>A disabled profile is neither fetched nor delivered; its stored jobs wait until it is enabled again.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Per-user settings that apply to all profiles (the filters moved to <see cref="SearchProfile"/>).</summary>
public class UserProfile
{
    public long ChatId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Delivery mode: instant per poll, or daily digest at DigestHourUtc.</summary>
    public DeliveryMode Delivery { get; set; } = DeliveryMode.Instant;
    public int DigestHourUtc { get; set; } = 9;
}

public enum DeliveryMode { Instant = 0, Digest = 1 }
