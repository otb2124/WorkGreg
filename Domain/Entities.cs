namespace WorkGreg.Domain;

/// <summary>How a source is fetched. Add new values here, then add an IJobFetcher for them.</summary>
public enum FetchType
{
    Feed = 0,     // RSS / Atom
    Html = 1,     // plain HTTP + HTML/JSON parsing (planned)
    Browser = 2   // headless browser (planned)
}

public class Source
{
    public int Id { get; set; }
    public long OwnerChatId { get; set; }
    public string Name { get; set; } = "";
    public FetchType Type { get; set; }
    public string Url { get; set; } = "";

    /// <summary>Catalog id of the source (see <see cref="SourceCatalog"/>), e.g. "dou". Null for legacy rows added with a raw URL.</summary>
    public string? Provider { get; set; }

    /// <summary>The search keyword sent to the provider. Url is derived from Provider + Keyword. Null for legacy rows.</summary>
    public string? Keyword { get; set; }

    /// <summary>Fetcher-specific settings (e.g. CSS selectors for Html). Unused by Feed.</summary>
    public string? ConfigJson { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>False until the first successful fetch; that fetch only records existing jobs without notifying.</summary>
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

    /// <summary>Normalized company+title hash, used to skip the same vacancy appearing on several boards.</summary>
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

public class UserProfile
{
    public long ChatId { get; set; }
    public string Include { get; set; } = "";
    public string Exclude { get; set; } = "";
    public bool RemoteOnly { get; set; }
    public bool Paused { get; set; }

    // NEW
    /// <summary>Only deliver jobs whose Company is in this comma list (empty = any).</summary>
    public string Companies { get; set; } = "";
    /// <summary>Only deliver jobs whose Location is in this comma list (empty = any).</summary>
    public string Locations { get; set; } = "";
    /// <summary>Min salary if the fetcher exposes it (nullable = no constraint).</summary>
    public int? MinSalary { get; set; }
    /// <summary>Delivery mode: instant per poll, or daily digest at DigestHourUtc.</summary>
    public DeliveryMode Delivery { get; set; } = DeliveryMode.Instant;
    public int DigestHourUtc { get; set; } = 9;
}

public enum DeliveryMode { Instant = 0, Digest = 1 }