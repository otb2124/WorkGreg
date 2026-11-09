using System.Text;
using WorkGreg.Client;
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

        var p = await client.TryLoadAsync(chatId, target.ProfileId, ct);
        if (p is null)
            return new("Profile not found", null, "That profile doesn't exist (anymore).", CallbackData.Profiles);

        var meta = await client.GetMetaAsync(ct);

        var filtersBack = CallbackData.Filters(p.Id);
        var profileBack = CallbackData.Profile(p.Id);

        return target.Kind switch
        {
            PromptKind.Keyword => new("✏️ Keyword", p.Keyword,
                "Send the new keyword. Every source of this profile will search for it. " +
                "Vacancies already found are kept; only what is searched changes.",
                profileBack),

            PromptKind.Rename => new("🏷 Rename profile", p.Name,
                $"Send a new name (up to {meta.MaxNameLength} characters).",
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
}