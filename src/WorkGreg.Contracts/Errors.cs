namespace WorkGreg.Services;

/// <summary>
/// Stable ids of every error the core can answer. They are part of the API: never rename one, only add.
/// Dotted lower case, "area.what_happened".
/// </summary>
public static class ErrorCodes
{
    /// <summary>A client got an error body it could not classify (for example from an older core that sent no code).</summary>
    public const string Unknown = "error.unknown";

    /// <summary>The request itself is malformed (HTTP API only).</summary>
    public const string InvalidRequest = "request.invalid";

    public const string ProfileNotFound = "profile.not_found";
    public const string ProfileLimitReached = "profile.limit_reached";
    public const string ProfileKeywordInvalid = "profile.keyword_invalid";
    public const string ProfileNameInvalid = "profile.name_invalid";
    public const string MinSalaryInvalid = "profile.min_salary_invalid";

    public const string SourceNotInProfile = "source.not_in_profile";
    public const string SourceUnknown = "source.unknown";
    public const string SourceNotImplemented = "source.not_implemented";
    public const string SourceAlreadyAdded = "source.already_added";
    public const string SourceNameRequired = "source.name_required";

    public const string DigestHourInvalid = "settings.digest_hour_invalid";

    public const string CheckAlreadyRunning = "check.already_running";
    public const string CheckHourlyRunning = "check.hourly_running";

    /// <summary>A prompt answer that had to be a positive whole number.</summary>
    public const string InputPositiveInteger = "input.positive_integer";

    // ---- autofill
    public const string AutofillValueInvalid = "autofill.value_invalid";
    public const string AutofillPatternInvalid = "autofill.pattern_invalid";
    public const string AutofillRegexInvalid = "autofill.regex_invalid";
    public const string AutofillRuleLimitReached = "autofill.rule_limit_reached";
    public const string AutofillRuleNotFound = "autofill.rule_not_found";
    public const string AutofillDocumentTooLarge = "autofill.document_too_large";
    public const string AutofillDocumentTypeInvalid = "autofill.document_type_invalid";
    public const string AutofillDocumentNameInvalid = "autofill.document_name_invalid";
    public const string AutofillCopySameProfile = "autofill.copy_same_profile";
    public const string AutofillConflict = "autofill.conflict";

    // ---- applications
    public const string DeliveryNotFound = "delivery.not_found";
    public const string ApplicationNotFound = "application.not_found";
    public const string ApplicationStateConflict = "application.state_conflict";
}

/// <summary>
/// The one place where a code, its kind, its English message and its arguments are written down together.
/// Services return these; nothing else builds an <see cref="Error"/>. Limits come in as arguments because the
/// constants live in the core and this project cannot see them.
/// </summary>
public static class Errors
{
    // ---- request shape (HTTP API)

    public static Error InvalidRequest(string message) => Error.Validation(ErrorCodes.InvalidRequest, message);

    // ---- profiles

    public static Error ProfileNotFound { get; } = Error.NotFound(ErrorCodes.ProfileNotFound, "No such profile.");

    public static Error ProfileLimitReached(int max) =>
        Error.Validation(ErrorCodes.ProfileLimitReached, $"Profile limit reached ({max}).", Error.Arg("max", max));

    public static Error KeywordInvalid(int max) =>
        Error.Validation(ErrorCodes.ProfileKeywordInvalid, $"The keyword must be 1-{max} characters.", Error.Arg("max", max));

    public static Error NameInvalid(int max) =>
        Error.Validation(ErrorCodes.ProfileNameInvalid, $"The name must be 1-{max} characters.", Error.Arg("max", max));

    public static Error MinSalaryInvalid { get; } =
        Error.Validation(ErrorCodes.MinSalaryInvalid, "The minimum salary must be a positive number.");

    // ---- sources

    public static Error SourceNotInProfile { get; } =
        Error.NotFound(ErrorCodes.SourceNotInProfile, "That source is not part of this profile.");

    public static Error SourceNameRequired { get; } = Error.Validation(ErrorCodes.SourceNameRequired, "Name a source.");

    public static Error UnknownSource(string id, string available) =>
        Error.Validation(
            ErrorCodes.SourceUnknown,
            $"Unknown source '{id}'. Available: {available}.",
            Error.Arg("id", id, "available", available));

    public static Error SourceNotImplemented(string name) =>
        Error.Validation(ErrorCodes.SourceNotImplemented, $"{name} is planned but not implemented yet.", Error.Arg("name", name));

    public static Error SourceAlreadyAdded(string name) =>
        Error.Conflict(ErrorCodes.SourceAlreadyAdded, $"{name} is already a source of this profile.", Error.Arg("name", name));

    // ---- settings

    public static Error DigestHourInvalid(int lastHour) =>
        Error.Validation(
            ErrorCodes.DigestHourInvalid,
            $"The digest hour must be between 0 and {lastHour} (UTC).",
            Error.Arg("max", lastHour));

    // ---- checks

    public static Error CheckAlreadyRunning { get; } = Error.Busy(ErrorCodes.CheckAlreadyRunning, "A check is already running.");

    public static Error HourlyCheckRunning { get; } =
        Error.Busy(ErrorCodes.CheckHourlyRunning, "The hourly check is running right now.");

    // ---- autofill

    public static Error AutofillValueInvalid(int max) =>
        Error.Validation(ErrorCodes.AutofillValueInvalid, $"The value must be 1-{max} characters.", Error.Arg("max", max));

    public static Error AutofillPatternInvalid(int max) =>
        Error.Validation(
            ErrorCodes.AutofillPatternInvalid,
            $"The pattern must be 1-{max} characters and contain a letter or a digit.",
            Error.Arg("max", max));

    public static Error AutofillRegexInvalid { get; } =
        Error.Validation(ErrorCodes.AutofillRegexInvalid, "That is not a valid regular expression.");

    public static Error AutofillRuleLimitReached(int max) =>
        Error.Validation(ErrorCodes.AutofillRuleLimitReached, $"Custom answer limit reached ({max}).", Error.Arg("max", max));

    public static Error AutofillRuleNotFound { get; } =
        Error.NotFound(ErrorCodes.AutofillRuleNotFound, "No such custom answer.");

    public static Error AutofillDocumentTooLarge(int maxMb) =>
        Error.Validation(ErrorCodes.AutofillDocumentTooLarge, $"The file is larger than {maxMb} MB.", Error.Arg("max_mb", maxMb));

    public static Error AutofillDocumentTypeInvalid { get; } =
        Error.Validation(ErrorCodes.AutofillDocumentTypeInvalid, "Only PDF and DOCX files are accepted.");

    public static Error AutofillDocumentNameInvalid { get; } =
        Error.Validation(ErrorCodes.AutofillDocumentNameInvalid, "The file name is empty or too long.");

    public static Error AutofillCopySameProfile { get; } =
        Error.Validation(ErrorCodes.AutofillCopySameProfile, "Pick a different profile to copy from.");

    public static Error AutofillConflict { get; } =
        Error.Conflict(ErrorCodes.AutofillConflict, "The settings changed at the same moment. Please try again.");

    // ---- applications

    /// <summary>Also the answer for a vacancy that belongs to somebody else: the two are not told apart.</summary>
    public static Error DeliveryNotFound { get; } =
        Error.NotFound(ErrorCodes.DeliveryNotFound, "That vacancy is no longer available.");

    public static Error ApplicationNotFound { get; } =
        Error.NotFound(ErrorCodes.ApplicationNotFound, "No such application.");

    public static Error ApplicationStateConflict(string status) =>
        Error.Conflict(
            ErrorCodes.ApplicationStateConflict,
            $"This vacancy is already marked as {status}.",
            Error.Arg("status", status));

    // ---- front-end input

    public static Error PositiveWholeNumberExpected { get; } =
        Error.Validation(ErrorCodes.InputPositiveInteger, "Please send a positive whole number.");
}
