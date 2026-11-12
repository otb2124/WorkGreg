namespace WorkGreg.Domain;

/// <summary>The standard values a user can store per search profile. Stored as text, so the order here is free to change.</summary>
public enum AutofillKey
{
    FirstName = 0,
    LastName = 1,
    Email = 2,
    Phone = 3,
    City = 4,
    Country = 5,
    LinkedIn = 6,
    GitHub = 7,
    Website = 8,
    Salary = 9,
    NoticePeriod = 10,
    CoverLetter = 11
}

/// <summary>A <c>Standard</c> row fills one <see cref="AutofillKey"/>; a <c>Custom</c> row is a "question pattern -> answer" rule.</summary>
public enum AutofillFieldKind { Standard = 0, Custom = 1 }

/// <summary>Which part of a form field a custom rule's pattern is compared with.</summary>
public enum FieldMatchBy { Any = 0, Label = 1, Name = 2, Id = 3, Placeholder = 4 }

public enum AutofillDocumentKind { Cv = 0, CoverLetterFile = 1 }

public enum AtsKind { Unknown = 0, Greenhouse = 1, Lever = 2, Ashby = 3, Generic = 4 }

/// <summary>
/// Lifecycle of one application run.
/// Allowed: Queued -> Filling -> Ready | NeedsInput | Failed; NeedsInput -> Queued; Ready -> Submitting -> Submitted | Failed;
/// Queued | Filling | NeedsInput | Ready -> Cancelled. AppliedManually and Skipped are set from the tracker buttons.
/// Terminal: Submitted, Failed, Cancelled, AppliedManually, Skipped.
/// </summary>
public enum ApplyStatus
{
    Queued = 0,
    Filling = 1,
    NeedsInput = 2,
    Ready = 3,
    Submitting = 4,
    Submitted = 5,
    Failed = 6,
    Cancelled = 7,
    AppliedManually = 8,
    Skipped = 9
}