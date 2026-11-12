using System.Text.Json.Serialization;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <summary>
/// Opens an application page, fills the form from the profile's settings and reports what happened.
/// It never submits. Core only knows this interface; the browser implementation lives in WorkGreg.Applier.
/// </summary>
public interface IFormFiller
{
    Task<FillReport> FillAsync(FillRequest request, CancellationToken ct);
}

/// <summary>The CV file to upload. The bytes stay in memory; nothing is written to disk.</summary>
public sealed record FillDocument(string FileName, string ContentType, byte[] Content);

/// <summary>The vacancy the form belongs to. Only used to expand cover-letter tokens ({{Company}}, {{Title}}).</summary>
public sealed record FillJobContext(string Title, string Company);

/// <param name="Url">The application page (absolute http/https).</param>
/// <param name="Ats">Hint from <c>AtsDetector</c>. Unknown / Generic use the generic filler.</param>
/// <param name="StandardValues">Standard values by key (resolved from the profile's settings).</param>
/// <param name="CustomRules">Custom "question pattern -> answer" rules, in evaluation order.</param>
/// <param name="Cv">Null when the profile has no CV stored.</param>
public sealed record FillRequest(
    string Url,
    AtsKind Ats,
    IReadOnlyDictionary<AutofillKey, string> StandardValues,
    IReadOnlyList<CustomRule> CustomRules,
    FillDocument? Cv,
    FillJobContext Job);

/// <summary>A required field nothing could be entered into. <paramref name="Id"/> is the element id (or name) so the user can tell similar labels apart.</summary>
public sealed record UnmatchedField(string Label, string Id);

/// <summary>
/// What a fill run did. Never contains entered values: only labels, ids and flags.
/// </summary>
/// <param name="FilledLabels">Labels of the fields that were filled.</param>
/// <param name="UnmatchedRequired">Required fields that are still empty.</param>
/// <param name="CaptchaDetected">True: nothing was filled or clicked; the user has to continue by hand.</param>
/// <param name="Fingerprint">SHA-256 of the sorted normalized field labels (see <see cref="FormFingerprint"/>); empty when the form was not scanned.</param>
/// <param name="Screenshot">Image of the page after filling (null if taking it failed). Kept out of JSON: it is stored separately.</param>
public sealed record FillReport(
    IReadOnlyList<string> FilledLabels,
    IReadOnlyList<UnmatchedField> UnmatchedRequired,
    bool CaptchaDetected,
    string Fingerprint,
    [property: JsonIgnore] byte[]? Screenshot);
