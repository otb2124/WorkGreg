using System.Text;
using WorkGreg.Client;
using WorkGreg.Domain;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>
/// "Send me the value" page. Not a button target of its own: <see cref="AskHandler"/> opens it, and
/// <see cref="PendingInputHandler"/> shows it again with a warning when the answer was not acceptable.
/// </summary>
internal sealed class PromptScreen(IWorkGregClient client)
{
    private sealed record Field(string Title, string? Current, string Help, CallbackData Back);

    public async Task<Screen> RenderAsync(long chatId, PromptTarget target, string? notice, CancellationToken ct)
    {
        var f = await DescribeAsync(chatId, target, ct);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>{f.Title}</b>");
        if (f.Current is not null)
            sb.AppendLine($"Current: {(f.Current.Length > 0 ? Html(Short(f.Current)) : "<i>not set</i>")}");
        sb.AppendLine();
        sb.AppendLine(f.Help);
        sb.AppendLine();
        sb.Append("<i>Send your answer as a message.</i>");

        var row = new List<InlineKeyboardButton>();
        if (target.IsFilter) row.Add(Btn("🗑 Clear", CallbackData.Clear(target)));
        row.Add(Btn("✖ Cancel", CallbackData.Cancel(f.Back)));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(new[] { row.ToArray() }));
    }

    private async Task<Field> DescribeAsync(long chatId, PromptTarget target, CancellationToken ct)
    {
        if (target.Kind == PromptKind.NewProfile)
            return new("➕ New profile", null,
                "Send the keyword to search for, e.g. <code>full stack dev</code> or <code>it support</code>.\n" +
                "It also becomes the profile's name (you can rename it later). Afterwards you pick its sources and filters.",
                CallbackData.Profiles);

        if (target.IsAutofill) return await DescribeAutofillAsync(chatId, target, ct);

        var p = await client.TryLoadAsync(chatId, target.ProfileId, ct);
        if (p is null)
            return new("Profile not found", null, "That profile doesn't exist (anymore).", CallbackData.Profiles);

        // Only the rename prompt needs the core's limits; the others never ask for them.
        var maxNameLength = target.Kind == PromptKind.Rename
            ? (await client.GetMetaAsync(ct)).MaxNameLength
            : 0;

        var filtersBack = CallbackData.Filters(p.Id);
        var profileBack = CallbackData.Profile(p.Id);

        return target.Kind switch
        {
            PromptKind.Keyword => new("✏️ Keyword", p.Keyword,
                "Send the new keyword. Every source of this profile will search for it. " +
                "Vacancies already found are kept; only what is searched changes.",
                profileBack),

            PromptKind.Rename => new("🏷 Rename profile", p.Name,
                $"Send a new name (up to {maxNameLength} characters).",
                profileBack),

            PromptKind.Include => new("🔤 Include keywords", ListText(p.Include),
                "Send keywords separated by commas, e.g. <code>.NET, C#, Angular</code>.\n" +
                "A vacancy matches if <b>any</b> keyword appears in its title, company, location or description.\n" +
                "Tip: titles are often hyphenated, so add <code>full-stack</code> next to <code>full stack</code>.",
                filtersBack),

            PromptKind.Exclude => new("🚫 Exclude keywords", ListText(p.Exclude),
                "Vacancies whose <b>title</b> contains any of these are skipped, e.g. <code>senior, lead, principal</code>.",
                filtersBack),

            PromptKind.Companies => new("🏢 Companies", ListText(p.Companies),
                "Only vacancies from these companies, separated by commas.\n" +
                "⚠️ RSS feeds usually don't expose a company, so this can hide everything coming from feed sources.",
                filtersBack),

            PromptKind.Locations => new("📍 Locations", ListText(p.Locations),
                "Only vacancies in these locations, separated by commas, e.g. <code>Zagreb, Europe</code>.\n" +
                "⚠️ RSS feeds usually don't expose a location, so this can hide everything coming from feed sources.",
                filtersBack),

            PromptKind.MinSalary => new("💰 Minimum salary", p.MinSalary?.ToString() ?? "",
                "Send a number, e.g. <code>1000</code>.\n" +
                "⚠️ Vacancies with no reported salary are dropped, and feed sources don't report one yet.",
                filtersBack),

            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Unknown prompt kind.")
        };
    }

    /// <summary>The autofill prompts: a standard value, a new or edited custom answer, the CV. They read the autofill settings, not the profile.</summary>
    private async Task<Field> DescribeAutofillAsync(long chatId, PromptTarget target, CancellationToken ct)
    {
        var loaded = await client.GetAutofillAsync(chatId, target.ProfileId, ct);
        if (loaded.IsFailure)
            return new("Profile not found", null, "That profile doesn't exist (anymore).", CallbackData.Profiles);

        var settings = loaded.Value;
        var back = CallbackData.Autofill(target.ProfileId);

        switch (target.Kind)
        {
            case PromptKind.AutofillValue:
                {
                    var key = target.Key!.Value;
                    var current = settings.Values.FirstOrDefault(v => v.Key == key)?.Value ?? "";
                    return new($"✏️ {AutofillLabels.Of(key)}", current, ValueHelp(key), back);
                }

            case PromptKind.AutofillAddRule:
                return new("➕ Custom answer", null, AutofillRuleText.Help, back);

            case PromptKind.AutofillEditRule:
                {
                    var rule = settings.Rules.FirstOrDefault(r => r.Id == target.ItemId);
                    return rule is null
                        ? new("Answer not found", null, "That custom answer doesn't exist (anymore).", back)
                        : new("✏️ Custom answer", AutofillRuleText.Format(rule),
                            "Send the new line.\n" + AutofillRuleText.Help, CallbackData.AutofillRule(target.ProfileId, rule.Id));
                }

            case PromptKind.AutofillCv:
                {
                    var cv = settings.Documents.FirstOrDefault(d => d.Kind == AutofillDocumentKind.Cv);
                    return new("📎 CV", cv?.FileName ?? "",
                        "Send your CV as a <b>file</b> (PDF or DOCX, up to 5 MB), not as a photo. It replaces the current one.", back);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Not an autofill prompt.");
        }
    }

    private static string ValueHelp(AutofillKey key) => key switch
    {
        AutofillKey.FirstName => "Send your first name.",
        AutofillKey.LastName => "Send your last name.",
        AutofillKey.Email => "Send the email address to put on applications.",
        AutofillKey.Phone => "Send your phone number the way a form should get it, e.g. <code>+385 91 234 5678</code>.",
        AutofillKey.City => "Send the city you live in.",
        AutofillKey.Country => "Send your country.",
        AutofillKey.LinkedIn => "Send the link to your LinkedIn profile.",
        AutofillKey.GitHub => "Send the link to your GitHub profile.",
        AutofillKey.Website => "Send the link to your website or portfolio.",
        AutofillKey.Salary => "Send the expected salary exactly as it should be typed, e.g. <code>2500</code> or <code>2500 EUR net</code>.",
        AutofillKey.NoticePeriod => "Send your notice period, e.g. <code>30 days</code>.",
        AutofillKey.CoverLetter =>
            "Send your cover letter as a template. Line breaks are kept. These words are replaced for each vacancy: " +
            "<code>{{Company}}</code>, <code>{{Title}}</code>, <code>{{FirstName}}</code>, <code>{{LastName}}</code>.",
        _ => "Send the value."
    };
}
