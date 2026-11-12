using WorkGreg.Domain;

namespace WorkGreg.Contracts;

/// <summary>One stored standard value, keyed by <see cref="AutofillKey"/>. A key that is not in the list is not set.</summary>
public sealed record AutofillValueDto(AutofillKey Key, string Value);

/// <summary>A custom "field pattern -> answer" rule. Rules are listed in evaluation order.</summary>
public sealed record AutofillRuleDto(int Id, FieldMatchBy MatchBy, string Pattern, bool IsRegex, string Value, int SortOrder);

/// <summary>Metadata of a stored file. The bytes never leave the core through the bot's API.</summary>
public sealed record AutofillDocumentDto(AutofillDocumentKind Kind, string FileName, string ContentType, int SizeBytes);

/// <summary>Everything stored for one profile's autofill. Every autofill call answers with the settings as they are afterwards.</summary>
public sealed record AutofillSettingsDto(
    int ProfileId,
    IReadOnlyList<AutofillValueDto> Values,
    IReadOnlyList<AutofillRuleDto> Rules,
    IReadOnlyList<AutofillDocumentDto> Documents);

/// <summary>PUT .../autofill/fields/{key}. The value is trimmed; blank is refused (use DELETE to clear).</summary>
public sealed record SetAutofillValueRequest(string? Value);

/// <summary>POST / PUT .../autofill/rules. A plain pattern is a normalized substring; IsRegex makes it a case-insensitive regular expression.</summary>
public sealed record AutofillRuleRequest(string Pattern, string Value, bool IsRegex = false, FieldMatchBy MatchBy = FieldMatchBy.Any);

/// <summary>POST .../autofill/copy on the target profile: replaces its autofill settings with a copy of another profile's.</summary>
public sealed record CopyAutofillRequest(int FromProfileId);