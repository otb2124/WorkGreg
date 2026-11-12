namespace WorkGreg.Domain;

/// <summary>Limits shared by the EF model and by AutofillService validation (one place, so they cannot drift apart).</summary>
public static class AutofillLimits
{
    public const int PatternMaxLength = 200;
    public const int ValueMaxLength = 300;
    public const int CoverLetterMaxLength = 4000;
    public const int MaxCustomRulesPerProfile = 50;
    public const int MaxDocumentBytes = 5 * 1024 * 1024;
}

/// <summary>
/// One thing entered into application forms for a <see cref="SearchProfile"/>.
/// A <see cref="AutofillFieldKind.Standard"/> row fills one <see cref="AutofillKey"/> (at most one row per profile and key).
/// A <see cref="AutofillFieldKind.Custom"/> row is a "field whose <see cref="MatchBy"/> matches <see cref="Pattern"/> gets <see cref="Value"/>" rule,
/// evaluated in <see cref="SortOrder"/>. Deleting the profile deletes its rows (cascade).
/// </summary>
public class AutofillField
{
    public int Id { get; set; }

    public int SearchProfileId { get; set; }
    public SearchProfile SearchProfile { get; set; } = null!;

    public AutofillFieldKind Kind { get; set; }

    /// <summary>Standard rows only; null for custom rows.</summary>
    public AutofillKey? Key { get; set; }

    /// <summary>Custom rows: which part of the form field the pattern is compared with.</summary>
    public FieldMatchBy MatchBy { get; set; } = FieldMatchBy.Any;

    /// <summary>Custom rows only (max <see cref="AutofillLimits.PatternMaxLength"/> chars).</summary>
    public string? Pattern { get; set; }
    public bool IsRegex { get; set; }

    /// <summary>
    /// What is entered. Max <see cref="AutofillLimits.ValueMaxLength"/> chars, except the <see cref="AutofillKey.CoverLetter"/>
    /// template (<see cref="AutofillLimits.CoverLetterMaxLength"/>); it may use {{Company}}, {{Title}}, {{FirstName}}, {{LastName}}.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>Custom rows are evaluated in this order (lowest first).</summary>
    public int SortOrder { get; set; }
}

/// <summary>A file attached to application forms (CV, cover-letter file). One per profile and kind: uploading replaces.</summary>
public class AutofillDocument
{
    public int Id { get; set; }

    public int SearchProfileId { get; set; }
    public SearchProfile SearchProfile { get; set; } = null!;

    public AutofillDocumentKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";

    /// <summary>Max <see cref="AutofillLimits.MaxDocumentBytes"/>; PDF or DOCX only (checked by extension and magic bytes in the service).</summary>
    public byte[] Content { get; set; } = [];
}

/// <summary>
/// One application attempt for one vacancy. It is history: it outlives the job (retention deletes old jobs) and the profile,
/// so <see cref="JobId"/> and <see cref="SearchProfileId"/> become null instead of cascading. Title, Company and Url are snapshots.
/// </summary>
public class Application
{
    public int Id { get; set; }
    public long OwnerChatId { get; set; }

    public int? SearchProfileId { get; set; }
    public SearchProfile? SearchProfile { get; set; }

    public int? JobId { get; set; }
    public Job? Job { get; set; }

    /// <summary>Copy of <see cref="Job.Hash"/>, so "already applied to this one under another board" still works after the job is purged.</summary>
    public string JobHash { get; set; } = "";

    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Url { get; set; } = "";

    public AtsKind Ats { get; set; }
    public ApplyStatus Status { get; set; } = ApplyStatus.Queued;

    /// <summary>What was filled, which fields were unmatched, the form fingerprint. Never contains user-entered values in logs.</summary>
    public string? ReportJson { get; set; }

    /// <summary>Purged after the retention period; the row itself is never deleted.</summary>
    public byte[]? Screenshot { get; set; }
    public string? Error { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
}