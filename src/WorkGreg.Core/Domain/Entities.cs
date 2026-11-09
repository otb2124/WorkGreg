namespace WorkGreg.Domain;

/// <summary>How a source is fetched. Add new values here, then add an IJobFetcher for them.</summary>

/// <summary>
/// One site that a <see cref="SearchProfile"/> reads from. The search keyword is NOT stored here:
/// it belongs to the profile, so changing it once updates every source of that profile.
/// </summary>
public class Source
{
    public int Id { get; set; }

    /// <summary>
    /// The profile this source belongs to (real foreign key, cascade delete). A site used by two profiles is two Source rows.
    /// The owner of a source is <c>SearchProfile.OwnerChatId</c>; it is not stored twice.
    /// </summary>
    public int SearchProfileId { get; set; }
    public SearchProfile SearchProfile { get; set; } = null!;

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

    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
}

/// <summary>
/// A vacancy exactly as a provider published it. Stored ONCE per (<see cref="Provider"/>, <see cref="ExternalId"/>), however many
/// users, profiles or keyword searches found it. Nothing here is about a user: who was told about it, and whether,
/// lives in <see cref="JobDelivery"/>.
/// </summary>
public class Job
{
    public int Id { get; set; }

    /// <summary>
    /// Catalog id of the provider that published it (e.g. "remotive"); "source{Id}" for legacy rows added with a raw URL
    /// (the same scope <see cref="JobHasher"/> uses). Together with <see cref="ExternalId"/> this is the identity of a vacancy.
    /// </summary>
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";

    /// <summary>Normalized company+title hash, used to skip the same vacancy appearing on several boards (or in several profiles).</summary>
    public string Hash { get; set; } = "";

    public string Title { get; set; } = "";
    public string? Company { get; set; }
    public string? Location { get; set; }
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? PostedUtc { get; set; }

    /// <summary>When ANY source first stored this vacancy. Per-user "first seen" is <see cref="JobDelivery.CreatedUtc"/>.</summary>
    public DateTime FirstSeenUtc { get; set; }
    public int? Salary { get; set; }            // in Job, for MinSalary filter
    public string? EmploymentType { get; set; } // full-time / contract
}

/// <summary>
/// One user's state for one stored vacancy, as found through one source (so through one profile). This is the per-user half
/// of the ingest/delivery split: <see cref="Job"/> is shared and immutable, this row says whether the filters were
/// evaluated and whether a Telegram message was sent.
/// Deleting the job or the source deletes the delivery (cascade); deleting a delivery never touches the job.
/// </summary>
public class JobDelivery
{
    public int Id { get; set; }

    public int JobId { get; set; }
    public Job Job { get; set; } = null!;

    /// <summary>The source that found the job. Its profile decides the filters, its profile's owner is who receives it.</summary>
    public int SourceId { get; set; }

    /// <summary>
    /// Deliberate denormalization: the owner of the source's profile, copied once when the delivery is created
    /// (SourceSyncService). It keeps the per-user queries and the duplicate check free of joins through Source and SearchProfile.
    /// </summary>
    public long OwnerChatId { get; set; }

    /// <summary>When this user's source first stored the job (retention is judged by this, not by the job's own first-seen time).</summary>
    public DateTime CreatedUtc { get; set; }

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

    /// <summary>A job matches if ANY of these appears in its title, company, location or description (empty = anything).</summary>
    public List<string> Include { get; set; } = [];
    /// <summary>Jobs whose title contains any of these are skipped (empty = nothing excluded).</summary>
    public List<string> Exclude { get; set; } = [];
    /// <summary>Only deliver jobs whose Company matches one of these (empty = any).</summary>
    public List<string> Companies { get; set; } = [];
    /// <summary>Only deliver jobs whose Location matches one of these (empty = any).</summary>
    public List<string> Locations { get; set; } = [];
    /// <summary>Min salary if the fetcher exposes it (null = no constraint).</summary>
    public int? MinSalary { get; set; }
    public bool RemoteOnly { get; set; }

    /// <summary>A disabled profile is neither fetched nor delivered; its stored jobs wait until it is enabled again.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Per-user settings that apply to all profiles (the filters live on <see cref="SearchProfile"/>).</summary>
public class UserSettings
{
    public long ChatId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Delivery mode: instant per poll, or daily digest at DigestHourUtc.</summary>
    public DeliveryMode Delivery { get; set; } = DeliveryMode.Instant;
    public int DigestHourUtc { get; set; } = 9;

    /// <summary>When the last digest was handed over (UTC). Null = never; see <see cref="DigestSchedule"/>.</summary>
    public DateTime? LastDigestSentUtc { get; set; }
}