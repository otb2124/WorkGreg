using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>A file the user sent: just what the bot needs to decide whether to download it. The bytes are fetched later.</summary>
internal sealed record IncomingDocument(string FileId, string? FileName, string? MimeType, long? Size);

/// <summary>
/// Plain text (or a file) as the answer to a prompt ("send me your keywords"). Validation and saving are the core's job;
/// what each prompt kind calls and where it goes afterwards is one row of <see cref="PromptSpecs"/>.
/// The autofill prompts (a value, a custom answer, the CV) answer with autofill settings, not with a profile, so they
/// have their own methods below instead of rows in that table.
/// </summary>
internal sealed class PendingInputHandler(
    IWorkGregClient client,
    PendingInputStore pending,
    PromptScreen promptScreen,
    ProfileScreen profileScreen,
    FiltersScreen filtersScreen,
    AutofillScreen autofillScreen,
    AutofillRulesScreen rulesScreen,
    IBotIo io)
{
    // The core is the authority (AutofillLimits.MaxDocumentBytes); this only avoids downloading a file that cannot be accepted.
    private const long MaxDocumentBytes = 5 * 1024 * 1024;

    private const string PdfMime = "application/pdf";
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Returns null when nothing was waiting for an answer (or the prompt has expired).</summary>
    public async Task<TextResult?> HandleAsync(long chatId, string text, CancellationToken ct)
    {
        var pend = pending.Get(chatId);
        if (pend is null) return null;

        if (pend.Target.IsAutofill) return await HandleAutofillTextAsync(chatId, pend, text, ct);

        var (kind, profileId) = pend.Target;
        var spec = PromptSpecs.Of(kind);

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var input = string.Join(spec.IsList ? ", " : " ", lines);
        var clear = spec.Clearable && input is "-" or "clear" or "off";

        var result = await spec.Save(client, chatId, profileId, clear ? null : input, ct);

        // A validation error: ask again.
        if (result.IsFailure)
            return await AskAgainAsync(chatId, pend, Fail(result.Error), ct);

        pending.Clear(chatId);
        var profile = result.Value;
        var notice = Ok(spec.Done(profile));

        Screen screen = spec.Return switch
        {
            PromptReturn.NewProfile => await profileScreen.RenderAsync(chatId, profile.Id, notice, ct),
            PromptReturn.Profile => await profileScreen.RenderAsync(chatId, profileId, notice, ct),
            _ => await filtersScreen.RenderAsync(chatId, profileId, notice, ct)
        };

        return new TextResult(pend.MessageId, screen);
    }

    /// <summary>
    /// A file as the answer to a prompt. Only the CV prompt takes one. Returns null when nothing was waiting for an answer.
    /// Nothing about the file (its name or content) is logged.
    /// </summary>
    public async Task<TextResult?> HandleDocumentAsync(long chatId, IncomingDocument doc, CancellationToken ct)
    {
        var pend = pending.Get(chatId);
        if (pend is null) return null;

        if (pend.Target.Kind != PromptKind.AutofillCv)
            return await AskAgainAsync(chatId, pend, Warn("I need a text answer here, not a file."), ct);

        var name = FileNameOf(doc);
        var extension = name is null ? "" : Path.GetExtension(name);
        if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return await AskAgainAsync(chatId, pend, Warn("Only PDF and DOCX files are accepted."), ct);

        if (doc.Size > MaxDocumentBytes)
            return await AskAgainAsync(chatId, pend, Warn($"That file is too big. The limit is {MaxDocumentBytes / (1024 * 1024)} MB."), ct);

        var bytes = await io.DownloadFileAsync(doc.FileId, ct);
        var result = await client.SetAutofillDocumentAsync(chatId, pend.Target.ProfileId, AutofillDocumentKind.Cv, name!, bytes, ct);

        return await FinishAutofillAsync(chatId, pend, result, $"CV saved: <b>{Html(MenuKit.Short(name!, 60))}</b>.", backToRules: false, ct);
    }

    // ------------------------------------------------------------------ autofill prompts

    private async Task<TextResult> HandleAutofillTextAsync(long chatId, PendingInput pend, string text, CancellationToken ct)
    {
        var target = pend.Target;
        var profileId = target.ProfileId;

        switch (target.Kind)
        {
            case PromptKind.AutofillValue:
                {
                    var key = target.Key!.Value;

                    // A cover-letter template keeps its line breaks; every other value is one line.
                    var value = key == AutofillKey.CoverLetter ? text.Trim() : OneLine(text);

                    var saved = await client.SetAutofillValueAsync(chatId, profileId, key, value, ct);
                    return await FinishAutofillAsync(chatId, pend, saved, $"{AutofillLabels.Of(key)} saved.", backToRules: false, ct);
                }

            case PromptKind.AutofillAddRule:
                {
                    if (!AutofillRuleText.TryParse(OneLine(text), FieldMatchBy.Any, out var rule))
                        return await AskAgainAsync(chatId, pend, Warn(AutofillRuleText.FormatHint), ct);

                    var added = await client.AddAutofillRuleAsync(chatId, profileId, rule, ct);
                    return await FinishAutofillAsync(chatId, pend, added, "Custom answer added.", backToRules: false, ct);
                }

            case PromptKind.AutofillEditRule:
                {
                    // The line cannot say which part of the field the pattern is compared with, so keep what the rule had.
                    var current = await client.GetAutofillAsync(chatId, profileId, ct);
                    if (current.IsFailure)
                        return await FinishAutofillAsync(chatId, pend, current.Error, "", backToRules: true, ct);

                    var existing = current.Value.Rules.FirstOrDefault(r => r.Id == target.ItemId);
                    if (existing is null)
                        return await FinishAutofillAsync(chatId, pend, Errors.AutofillRuleNotFound, "", backToRules: true, ct);

                    if (!AutofillRuleText.TryParse(OneLine(text), existing.MatchBy, out var rule))
                        return await AskAgainAsync(chatId, pend, Warn(AutofillRuleText.FormatHint), ct);

                    var updated = await client.UpdateAutofillRuleAsync(chatId, profileId, target.ItemId, rule, ct);
                    return await FinishAutofillAsync(chatId, pend, updated, "Custom answer updated.", backToRules: true, ct);
                }

            case PromptKind.AutofillCv:
                return await AskAgainAsync(chatId, pend, Warn("Send the CV as a file (PDF or DOCX), not as text."), ct);

            default:
                throw new InvalidOperationException($"{target.Kind} is not an autofill prompt.");
        }
    }

    /// <summary>
    /// A failure asks again, except "not found" (the profile or the answer is gone): there is nothing left to ask about,
    /// so the prompt ends. A success ends it too and shows the screen the prompt came from.
    /// </summary>
    private async Task<TextResult> FinishAutofillAsync(
        long chatId, PendingInput pend, Result<AutofillSettingsDto, Error> result, string done, bool backToRules, CancellationToken ct)
    {
        var profileId = pend.Target.ProfileId;

        if (result.IsFailure)
        {
            if (result.Error.Kind != ErrorKind.NotFound)
                return await AskAgainAsync(chatId, pend, Fail(result.Error), ct);

            pending.Clear(chatId);
            return new TextResult(pend.MessageId, await autofillScreen.RenderAsync(chatId, profileId, Fail(result.Error), ct));
        }

        pending.Clear(chatId);
        var notice = Ok(done);
        var screen = backToRules
            ? await rulesScreen.RenderAsync(chatId, profileId, notice, ct, result.Value)
            : await autofillScreen.RenderAsync(chatId, profileId, notice, ct, result.Value);

        return new TextResult(pend.MessageId, screen);
    }

    /// <summary>Several lines become one, joined with a space.</summary>
    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The name Telegram gave the file, or (when it gave none) one made from its type. Null when it is neither.</summary>
    private static string? FileNameOf(IncomingDocument doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.FileName)) return doc.FileName;

        return doc.MimeType switch
        {
            PdfMime => "cv.pdf",
            DocxMime => "cv.docx",
            _ => null
        };
    }

    private async Task<TextResult> AskAgainAsync(long chatId, PendingInput pend, string notice, CancellationToken ct)
    {
        pending.Set(chatId, pend);   // the question is asked again, so it gets a fresh time limit
        return new(pend.MessageId, await promptScreen.RenderAsync(chatId, pend.Target, notice, ct));
    }
}
