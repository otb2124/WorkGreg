using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;

namespace WorkGreg.Telegram.Menu;

/// <summary>The list of the user's profiles. Also where every "profile not found" ends up.</summary>
[Handles(CallbackAction.Profiles)]
internal sealed class ProfilesScreen(ProfileService profiles, PendingInputStore pending) : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) => RenderAsync(ctx.ChatId, null, ctx.Ct);

    public async Task<Screen> RenderAsync(long chatId, string? notice, CancellationToken ct)
    {
        var all = await profiles.ListAsync(chatId, ct);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📂 Profiles</b>");
        sb.AppendLine();
        sb.Append(all.Count == 0
            ? "<i>No profiles yet.</i> A profile is one job hunt: a keyword (e.g. <code>full stack dev</code>), its own filters and its own sources."
            : "Each profile has its own keyword, filters and sources. Tap one to edit it.");

        var rows = all
            .Select(d => Row(Btn($"{(d.Profile.Enabled ? "▶️" : "⏸")} {Short(d.Profile.Name, 28)}", CallbackData.Profile(d.Profile.Id))))
            .ToList();

        if (all.Count < ProfileRules.MaxProfilesPerUser)
            rows.Add(Row(Btn("➕ New profile", CallbackData.NewProfile)));
        rows.Add(Row(Btn("⬅ Back", CallbackData.Home)));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }
}