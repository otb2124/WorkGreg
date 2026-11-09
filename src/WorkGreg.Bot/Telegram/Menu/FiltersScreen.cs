using System.Text;
using WorkGreg.Client;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>The filters of a profile and a button for each.</summary>
[Handles(CallbackAction.Filters)]
internal sealed class FiltersScreen(IWorkGregClient client, ProfilesScreen profilesScreen, PendingInputStore pending)
    : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) =>
        RenderAsync(ctx.ChatId, ctx.Data.ProfileId, null, ctx.Ct);

    public async Task<Screen> RenderAsync(long chatId, int profileId, string? notice, CancellationToken ct)
    {
        var p = await client.TryLoadAsync(chatId, profileId, ct);
        if (p is null) return await profilesScreen.RenderAsync(chatId, Warn(NoSuchProfile), ct);

        string Show(IReadOnlyList<string> l, string empty) => l.Count > 0 ? Html(Short(ListText(l))) : $"<i>{empty}</i>";

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>🎯 Filters · {Html(p.Name)}</b>");
        sb.AppendLine("<i>Apply to every source of this profile.</i>");
        sb.AppendLine();
        sb.AppendLine($"🔤 Include: {Show(p.Include, "anything")}");
        sb.AppendLine($"🚫 Exclude: {Show(p.Exclude, "nothing")}");
        sb.AppendLine($"🏢 Companies: {Show(p.Companies, "any")}");
        sb.AppendLine($"📍 Locations: {Show(p.Locations, "any")}");
        sb.AppendLine($"💰 Min salary: {(p.MinSalary is { } minSalary ? minSalary.ToString() : "<i>any</i>")}");
        sb.AppendLine($"🏠 Remote only: {(p.RemoteOnly ? "yes" : "no")}");
        sb.AppendLine();
        sb.Append("<i>Tap a button to change it.</i>");

        var kb = Kb(
            Row(Btn("🔤 Include", CallbackData.Ask(PromptKind.Include, p.Id)),
                Btn("🚫 Exclude", CallbackData.Ask(PromptKind.Exclude, p.Id))),
            Row(Btn("🏢 Companies", CallbackData.Ask(PromptKind.Companies, p.Id)),
                Btn("📍 Locations", CallbackData.Ask(PromptKind.Locations, p.Id))),
            Row(Btn("💰 Min salary", CallbackData.Ask(PromptKind.MinSalary, p.Id)),
                Btn(p.RemoteOnly ? "🏠 Remote: ON ✅" : "🏠 Remote: OFF", CallbackData.ToggleRemote(p.Id))),
            Row(Btn("⬅ Back", CallbackData.Profile(p.Id))));

        return new Screen(sb.ToString(), kb);
    }
}