using System.Globalization;
using System.Text;
using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>How the bot names the standard autofill values, in the order they are listed.</summary>
internal static class AutofillLabels
{
    /// <summary>Every key, in enum order (First name, Last name, Email first).</summary>
    public static readonly IReadOnlyList<AutofillKey> Keys = Enum.GetValues<AutofillKey>();

    public static string Of(AutofillKey key) => key switch
    {
        AutofillKey.FirstName => "First name",
        AutofillKey.LastName => "Last name",
        AutofillKey.Email => "Email",
        AutofillKey.Phone => "Phone",
        AutofillKey.City => "City",
        AutofillKey.Country => "Country",
        AutofillKey.LinkedIn => "LinkedIn",
        AutofillKey.GitHub => "GitHub",
        AutofillKey.Website => "Website",
        AutofillKey.Salary => "Salary",
        AutofillKey.NoticePeriod => "Notice period",
        AutofillKey.CoverLetter => "Cover letter",
        _ => key.ToString()
    };
}

/// <summary>
/// The autofill settings of one profile: the standard values, the CV, the custom answers, and a button for each.
/// Every autofill change ends here (or on <see cref="AutofillRulesScreen"/>), drawn from the settings the core answered with.
/// </summary>
[Handles(CallbackAction.Autofill)]
internal sealed class AutofillScreen(IWorkGregClient client, ProfilesScreen profilesScreen, PendingInputStore pending)
    : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) =>
        RenderAsync(ctx.ChatId, ctx.Data.ProfileId, null, ctx.Ct);

    /// <param name="settings">The settings when the caller already has them (the answer of a change); read from the core when null.</param>
    public async Task<Screen> RenderAsync(
        long chatId, int profileId, string? notice, CancellationToken ct, AutofillSettingsDto? settings = null)
    {
        var p = await client.TryLoadAsync(chatId, profileId, ct);
        if (p is null) return await profilesScreen.RenderAsync(chatId, Warn(NoSuchProfile), ct);

        if (settings is null)
        {
            var loaded = await client.GetAutofillAsync(chatId, profileId, ct);
            if (loaded.IsFailure) return await profilesScreen.RenderAsync(chatId, Fail(loaded.Error), ct);
            settings = loaded.Value;
        }

        return Render(p, settings, notice);
    }

    public Screen Render(ProfileDto p, AutofillSettingsDto a, string? notice)
    {
        var values = a.Values.ToDictionary(v => v.Key, v => v.Value);
        var cv = a.Documents.FirstOrDefault(d => d.Kind == AutofillDocumentKind.Cv);
        var ready = values.ContainsKey(AutofillKey.FirstName)
                    && values.ContainsKey(AutofillKey.LastName)
                    && values.ContainsKey(AutofillKey.Email);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>🧩 AutoFill: {Html(p.Name)}</b>");
        sb.AppendLine("<i>What is entered into application forms for vacancies of this profile.</i>");
        sb.AppendLine();

        foreach (var key in AutofillLabels.Keys)
            sb.AppendLine($"{AutofillLabels.Of(key)}: {ValueText(key, values)}");

        sb.AppendLine($"CV: {(cv is null ? "<i>not set</i>" : $"{Html(Short(cv.FileName, 40))} ({SizeText(cv.SizeBytes)})")}");
        sb.AppendLine($"Custom answers: {a.Rules.Count}");
        sb.AppendLine();
        sb.Append(ready
            ? "✅ Ready for AutoFill."
            : "<i>Set first name, last name and email to make this profile ready for AutoFill.</i>");

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var key in AutofillLabels.Keys)
        {
            var edit = Btn($"✏️ {AutofillLabels.Of(key)}", CallbackData.AskValue(p.Id, key));
            rows.Add(values.ContainsKey(key)
                ? Row(edit, Btn("🗑", CallbackData.AutofillClearValue(p.Id, key)))
                : Row(edit));
        }

        rows.Add(cv is null
            ? Row(Btn("📎 Upload CV", CallbackData.AskCv(p.Id)))
            : Row(Btn("📎 Replace CV", CallbackData.AskCv(p.Id)), Btn("🗑", CallbackData.AutofillDeleteDoc(p.Id))));
        rows.Add(Row(Btn("➕ Add answer", CallbackData.AskAddRule(p.Id)),
            Btn($"📋 Answers ({a.Rules.Count})", CallbackData.AutofillRules(p.Id))));
        rows.Add(Row(Btn("📋 Copy from profile…", CallbackData.AutofillCopy(p.Id))));
        rows.Add(Row(Btn("⬅ Back", CallbackData.Profile(p.Id))));

        return new Screen(sb.ToString(), Kb(rows.ToArray()));
    }

    private static string ValueText(AutofillKey key, IReadOnlyDictionary<AutofillKey, string> values)
    {
        if (!values.TryGetValue(key, out var value) || value.Length == 0) return "<i>not set</i>";

        // A cover letter is long and private: say that it is there, do not print it.
        return key == AutofillKey.CoverLetter
            ? $"template, {value.Length} characters"
            : Html(Short(value, 40));
    }

    private static string SizeText(int bytes) =>
        bytes < 1024 * 1024
            ? $"{Math.Max(1, bytes / 1024)} KB"
            : $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB";
}
