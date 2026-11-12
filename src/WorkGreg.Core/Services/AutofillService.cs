using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkGreg.Data;
using WorkGreg.Domain;

namespace WorkGreg.Services;

/// <summary>A custom "question pattern -> answer" rule as the user typed it. Only the service sets Kind, SortOrder and the profile.</summary>
public sealed record CustomRuleDraft(string Pattern, string Value, bool IsRegex = false, FieldMatchBy MatchBy = FieldMatchBy.Any);

/// <summary>What is known about a stored file. The bytes are never part of it.</summary>
public sealed record AutofillDocumentInfo(AutofillDocumentKind Kind, string FileName, string ContentType, int SizeBytes);

/// <summary>Everything stored for one profile: standard values (ordered by key), custom rules (in evaluation order) and file metadata.</summary>
public sealed record AutofillSettings(
    int ProfileId,
    IReadOnlyList<AutofillField> StandardFields,
    IReadOnlyList<AutofillField> CustomRules,
    IReadOnlyList<AutofillDocumentInfo> Documents);

/// <summary>
/// Use cases for the per-profile autofill settings: validation, ownership checks, persistence.
/// Every method takes the calling chat id and answers <see cref="Errors.ProfileNotFound"/> for a profile that is not that
/// chat's, so another user's profile is indistinguishable from a missing one. Nothing here logs user-entered values.
/// </summary>
public sealed class AutofillService(AppDb db)
{
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const int MaxFileNameLength = 120;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // ---- reads

    public async Task<Result<AutofillSettings, Error>> GetSettingsAsync(long chatId, int profileId, CancellationToken ct)
    {
        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;
        return await LoadSettingsAsync(profileId, ct);
    }

    /// <summary>
    /// Ids of this chat's profiles that have a first name, last name and email stored: the profiles whose vacancies get
    /// the AutoFill button. One query for all profiles, so callers do not ask once per vacancy.
    /// </summary>
    public Task<IReadOnlySet<int>> ReadyProfileIdsAsync(long chatId, CancellationToken ct) =>
        LoadReadyProfileIdsAsync(db, chatId, ct);

    /// <summary>
    /// <see cref="ReadyProfileIdsAsync"/> for callers that hold an <see cref="AppDb"/> but no scoped service (the delivery pass
    /// loads it once per pass and reuses it for every vacancy of that pass).
    /// </summary>
    public static async Task<IReadOnlySet<int>> LoadReadyProfileIdsAsync(AppDb db, long chatId, CancellationToken ct)
    {
        var ids = await db.AutofillFields.AsNoTracking()
            .Where(f => f.SearchProfile.OwnerChatId == chatId
                        && f.Kind == AutofillFieldKind.Standard
                        && (f.Key == AutofillKey.FirstName || f.Key == AutofillKey.LastName || f.Key == AutofillKey.Email)
                        && f.Value != "")
            .GroupBy(f => f.SearchProfileId)
            .Where(g => g.Count() == 3)   // at most one row per (profile, key), so 3 rows = all three keys
            .Select(g => g.Key)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    // ---- standard fields

    /// <summary>Sets (or replaces) one standard value. A blank value is an error: use <see cref="ClearStandardFieldAsync"/>.</summary>
    public async Task<Result<AutofillField, Error>> SetStandardFieldAsync(
        long chatId, int profileId, AutofillKey key, string? rawValue, CancellationToken ct)
    {
        if (!Enum.IsDefined(key)) return Errors.InvalidRequest("Unknown autofill key.");

        var max = MaxValueLength(key);
        var value = (rawValue ?? "").Trim();
        if (value.Length == 0 || value.Length > max) return Errors.AutofillValueInvalid(max);

        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        var field = await db.AutofillFields
            .FirstOrDefaultAsync(f => f.SearchProfileId == profileId && f.Key == key, ct);

        if (field is null)
        {
            field = new AutofillField
            {
                SearchProfileId = profileId,
                Kind = AutofillFieldKind.Standard,
                Key = key,
                MatchBy = FieldMatchBy.Any,
                Value = value
            };
            db.AutofillFields.Add(field);
        }
        else
        {
            field.Value = value;
        }

        if (!await TrySaveAsync(ct)) return Errors.AutofillConflict;
        return field;
    }

    /// <summary>Removes a standard value. Clearing something that is not set is fine.</summary>
    public async Task<Result<Unit, Error>> ClearStandardFieldAsync(
        long chatId, int profileId, AutofillKey key, CancellationToken ct)
    {
        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        await db.AutofillFields
            .Where(f => f.SearchProfileId == profileId && f.Key == key)
            .ExecuteDeleteAsync(ct);

        return Unit.Value;
    }

    // ---- custom rules

    public async Task<Result<AutofillField, Error>> AddCustomRuleAsync(
        long chatId, int profileId, CustomRuleDraft draft, CancellationToken ct)
    {
        var valid = ValidateRule(draft);
        if (valid.IsFailure) return valid.Error;

        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        var custom = db.AutofillFields.Where(f => f.SearchProfileId == profileId && f.Kind == AutofillFieldKind.Custom);
        if (await custom.CountAsync(ct) >= AutofillLimits.MaxCustomRulesPerProfile)
            return Errors.AutofillRuleLimitReached(AutofillLimits.MaxCustomRulesPerProfile);

        var last = await custom.MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;

        var rule = new AutofillField
        {
            SearchProfileId = profileId,
            Kind = AutofillFieldKind.Custom,
            Key = null,
            SortOrder = last + 1
        };
        Apply(rule, valid.Value);
        db.AutofillFields.Add(rule);

        if (!await TrySaveAsync(ct)) return Errors.AutofillConflict;
        return rule;
    }

    public async Task<Result<AutofillField, Error>> UpdateCustomRuleAsync(
        long chatId, int profileId, int ruleId, CustomRuleDraft draft, CancellationToken ct)
    {
        var valid = ValidateRule(draft);
        if (valid.IsFailure) return valid.Error;

        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        var rule = await db.AutofillFields.FirstOrDefaultAsync(
            f => f.Id == ruleId && f.SearchProfileId == profileId && f.Kind == AutofillFieldKind.Custom, ct);
        if (rule is null) return Errors.AutofillRuleNotFound;

        Apply(rule, valid.Value);

        if (!await TrySaveAsync(ct)) return Errors.AutofillConflict;
        return rule;
    }

    public async Task<Result<Unit, Error>> DeleteCustomRuleAsync(
        long chatId, int profileId, int ruleId, CancellationToken ct)
    {
        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        var deleted = await db.AutofillFields
            .Where(f => f.Id == ruleId && f.SearchProfileId == profileId && f.Kind == AutofillFieldKind.Custom)
            .ExecuteDeleteAsync(ct);

        if (deleted == 0) return Errors.AutofillRuleNotFound;
        return Unit.Value;
    }

    // ---- documents

    /// <summary>
    /// Stores a file, replacing the profile's previous one of the same kind. Only PDF and DOCX up to 5 MB are accepted, and the
    /// extension must agree with the file's first bytes. The content type is derived from the extension: the one a client
    /// sends is not trusted.
    /// </summary>
    public async Task<Result<AutofillDocumentInfo, Error>> SetDocumentAsync(
        long chatId, int profileId, AutofillDocumentKind kind, string? fileName, byte[]? bytes, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) return Errors.InvalidRequest("Unknown document kind.");
        if (bytes is null || bytes.Length == 0) return Errors.AutofillDocumentTypeInvalid;
        if (bytes.Length > AutofillLimits.MaxDocumentBytes)
            return Errors.AutofillDocumentTooLarge(AutofillLimits.MaxDocumentBytes / (1024 * 1024));

        var name = CleanFileName(fileName);
        if (name is null) return Errors.AutofillDocumentNameInvalid;

        var contentType = DetectContentType(name, bytes);
        if (contentType is null) return Errors.AutofillDocumentTypeInvalid;

        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        var document = await db.AutofillDocuments
            .FirstOrDefaultAsync(d => d.SearchProfileId == profileId && d.Kind == kind, ct);

        if (document is null)
        {
            document = new AutofillDocument { SearchProfileId = profileId, Kind = kind };
            db.AutofillDocuments.Add(document);
        }

        document.FileName = name;
        document.ContentType = contentType;
        document.Content = bytes;

        if (!await TrySaveAsync(ct)) return Errors.AutofillConflict;
        return new AutofillDocumentInfo(kind, name, contentType, bytes.Length);
    }

    /// <summary>Removes a stored file. Removing one that is not there is fine.</summary>
    public async Task<Result<Unit, Error>> DeleteDocumentAsync(
        long chatId, int profileId, AutofillDocumentKind kind, CancellationToken ct)
    {
        if (!await OwnsAsync(chatId, profileId, ct)) return Errors.ProfileNotFound;

        await db.AutofillDocuments
            .Where(d => d.SearchProfileId == profileId && d.Kind == kind)
            .ExecuteDeleteAsync(ct);

        return Unit.Value;
    }

    // ---- copy

    /// <summary>
    /// Replaces the target profile's autofill settings (values, custom rules and files) with copies of the source's.
    /// All or nothing. Both profiles must belong to the chat. Returns the target's new settings.
    /// </summary>
    public async Task<Result<AutofillSettings, Error>> CopySettingsAsync(
        long chatId, int fromProfileId, int toProfileId, CancellationToken ct)
    {
        if (fromProfileId == toProfileId) return Errors.AutofillCopySameProfile;

        var owned = await db.SearchProfiles.CountAsync(
            p => (p.Id == fromProfileId || p.Id == toProfileId) && p.OwnerChatId == chatId, ct);
        if (owned != 2) return Errors.ProfileNotFound;

        var fields = await db.AutofillFields.AsNoTracking()
            .Where(f => f.SearchProfileId == fromProfileId).ToListAsync(ct);
        var documents = await db.AutofillDocuments.AsNoTracking()
            .Where(d => d.SearchProfileId == fromProfileId).ToListAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.AutofillFields.Where(f => f.SearchProfileId == toProfileId).ExecuteDeleteAsync(ct);
            await db.AutofillDocuments.Where(d => d.SearchProfileId == toProfileId).ExecuteDeleteAsync(ct);

            db.AutofillFields.AddRange(fields.Select(f => new AutofillField
            {
                SearchProfileId = toProfileId,
                Kind = f.Kind,
                Key = f.Key,
                MatchBy = f.MatchBy,
                Pattern = f.Pattern,
                IsRegex = f.IsRegex,
                Value = f.Value,
                SortOrder = f.SortOrder
            }));
            db.AutofillDocuments.AddRange(documents.Select(d => new AutofillDocument
            {
                SearchProfileId = toProfileId,
                Kind = d.Kind,
                FileName = d.FileName,
                ContentType = d.ContentType,
                Content = d.Content
            }));

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();   // the transaction rolls back when it is disposed
            return Errors.AutofillConflict;
        }

        return await LoadSettingsAsync(toProfileId, ct);
    }

    // ---- helpers

    /// <summary>Max length of a stored value: the cover-letter template is long, everything else is one short line.</summary>
    public static int MaxValueLength(AutofillKey key) =>
        key == AutofillKey.CoverLetter ? AutofillLimits.CoverLetterMaxLength : AutofillLimits.ValueMaxLength;

    private Task<bool> OwnsAsync(long chatId, int profileId, CancellationToken ct) =>
        db.SearchProfiles.AnyAsync(p => p.Id == profileId && p.OwnerChatId == chatId, ct);

    private async Task<AutofillSettings> LoadSettingsAsync(int profileId, CancellationToken ct)
    {
        var fields = await db.AutofillFields.AsNoTracking()
            .Where(f => f.SearchProfileId == profileId)
            .ToListAsync(ct);

        // The size comes from the database (length of the BLOB), so the file itself is not loaded.
        var documents = await db.AutofillDocuments.AsNoTracking()
            .Where(d => d.SearchProfileId == profileId)
            .OrderBy(d => d.Id)
            .Select(d => new { d.Kind, d.FileName, d.ContentType, Size = d.Content.Length })
            .ToListAsync(ct);

        return new AutofillSettings(
            profileId,
            fields.Where(f => f.Kind == AutofillFieldKind.Standard).OrderBy(f => f.Key).ToList(),
            fields.Where(f => f.Kind == AutofillFieldKind.Custom).OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToList(),
            documents.Select(d => new AutofillDocumentInfo(d.Kind, d.FileName, d.ContentType, d.Size)).ToList());
    }

    /// <summary>Saves, turning a constraint violation (two taps at once on a unique index) into false instead of an exception.</summary>
    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private readonly record struct ValidRule(string Pattern, string Value, bool IsRegex, FieldMatchBy MatchBy);

    private static Result<ValidRule, Error> ValidateRule(CustomRuleDraft draft)
    {
        if (!Enum.IsDefined(draft.MatchBy)) return Errors.InvalidRequest("Unknown match target.");

        var pattern = (draft.Pattern ?? "").Trim();
        if (pattern.Length == 0 || pattern.Length > AutofillLimits.PatternMaxLength)
            return Errors.AutofillPatternInvalid(AutofillLimits.PatternMaxLength);

        var value = (draft.Value ?? "").Trim();
        if (value.Length == 0 || value.Length > AutofillLimits.ValueMaxLength)
            return Errors.AutofillValueInvalid(AutofillLimits.ValueMaxLength);

        if (draft.IsRegex)
        {
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
            catch (ArgumentException) { return Errors.AutofillRegexInvalid; }
        }
        else if (FieldMatcher.Normalize(pattern).Length == 0)
        {
            // A plain pattern made only of punctuation normalizes to nothing and could never match a field.
            return Errors.AutofillPatternInvalid(AutofillLimits.PatternMaxLength);
        }

        return new ValidRule(pattern, value, draft.IsRegex, draft.MatchBy);
    }

    private static void Apply(AutofillField rule, ValidRule valid)
    {
        rule.Pattern = valid.Pattern;
        rule.Value = valid.Value;
        rule.IsRegex = valid.IsRegex;
        rule.MatchBy = valid.MatchBy;
    }

    /// <summary>The file name without any folder part or control characters; null when nothing usable is left or it is too long.</summary>
    private static string? CleanFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length is > 0 and <= MaxFileNameLength ? name : null;
    }

    /// <summary>The content type for a PDF or DOCX whose extension AND first bytes agree; null for anything else.</summary>
    private static string? DetectContentType(string fileName, byte[] bytes)
    {
        var span = bytes.AsSpan();
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" when span.StartsWith("%PDF"u8) => PdfContentType,
            // A DOCX is a zip; its first entry starts with the local file header "PK\x03\x04".
            ".docx" when span.StartsWith("PK\u0003\u0004"u8) => DocxContentType,
            _ => null
        };
    }
}
