using WorkGreg.Domain;

namespace WorkGreg.Contracts;

/// <summary>One application attempt or tracker entry (applied / skipped). The screenshot and the report never leave the core in this DTO.</summary>
public sealed record ApplicationDto(
    int Id,
    int? ProfileId,
    string Title,
    string Company,
    string Url,
    AtsKind Ats,
    ApplyStatus Status,
    string? Error,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? SubmittedUtc);

/// <summary>
/// What tapping 🤖 AutoFill answers while there is no browser yet: what would be entered for this vacancy, and what is missing.
/// <see cref="Ready"/> is false when the profile no longer has first name, last name and email (settings removed since delivery).
/// <see cref="Existing"/> is an application of the same user for the same vacancy (same company + title, on any board), if there is one.
/// </summary>
public sealed record AutofillPreviewDto(
    int DeliveryId,
    int ProfileId,
    string ProfileName,
    string Title,
    string Company,
    bool Ready,
    IReadOnlyList<AutofillValueDto> Values,
    IReadOnlyList<AutofillKey> Missing,
    bool HasCv,
    ApplicationDto? Existing);

/// <summary>How an <see cref="ApplyStatus"/> is worded in messages (lower case, so it fits into a sentence).</summary>
public static class ApplyStatusLabels
{
    public static string Of(ApplyStatus status) => status switch
    {
        ApplyStatus.AppliedManually => "applied",
        ApplyStatus.NeedsInput => "needs input",
        _ => status.ToString().ToLowerInvariant()
    };
}
