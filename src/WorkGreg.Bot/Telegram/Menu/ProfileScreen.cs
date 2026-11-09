using System.Text;
using WorkGreg.Client;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>One profile: status, keyword, filter count, sources with their health.</summary>
[Handles(CallbackAction.Profile)]
internal sealed class ProfileScreen(IWorkGregClient client, ProfilesScreen profilesScreen, PendingInputStore pending)
    : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) =>
        RenderAsync(ctx.ChatId, ctx.Data.ProfileId, null, ctx.Ct);

    public async Task<Screen> RenderAsync(long chatId, int profileId, string? notice, CancellationToken ct)
    {
        var p = await client.TryLoadAsync(chatId, profileId, ct);
        if (p is null) return await profilesScreen.RenderAsync(chatId, Warn(NoSuchProfile), ct);

        var attached = p.Sources;

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>{Html(p.Name)}</b>  #{p.Id}");
        sb.AppendLine();
        sb.AppendLine($"Status: {(p.Enabled ? "▶️ active" : "⏸ disabled (not fetched, nothing delivered)")}");
        sb.AppendLine($"🔎 Keyword: {Html(p.Keyword)}");
        sb.AppendLine($"🎯 Filters: {ActiveFilterCount(p)} active");

        if (attached.Count == 0)
        {
            sb.Append("📡 Sources: <i>none yet, tap Sources to add some.</i>");
        }
        else
        {
            sb.AppendLine("📡 Sources:");
            for (var i = 0; i < attached.Count; i++)
            {
                var s = attached[i];
                var status = s.ConsecutiveFailures > 0
                    ? $"❌ failing ({s.ConsecutiveFailures}x): {Html(Short(s.LastError ?? "", 120))}"
                    : s.LastSuccessUtc is null ? "⏳ not checked yet" : $"✅ ok · {s.LastSuccessUtc:yyyy-MM-dd HH:mm} UTC";
                var line = $"  • {Html(s.Name)}: {status}";
                if (i < attached.Count - 1) sb.AppendLine(line); else sb.Append(line);
            }
        }

        var kb = Kb(
            Row(Btn("✏️ Keyword", CallbackData.Ask(PromptKind.Keyword, p.Id)),
                Btn("🏷 Rename", CallbackData.Ask(PromptKind.Rename, p.Id))),
            Row(Btn("📡 Sources", CallbackData.Sources(p.Id)), Btn("🎯 Filters", CallbackData.Filters(p.Id))),
            Row(Btn(p.Enabled ? "⏸ Disable" : "▶️ Enable", CallbackData.ToggleEnabled(p.Id))),
            Row(Btn("🗑 Remove profile", CallbackData.RemoveAsk(p.Id)), Btn("⬅ Back", CallbackData.Profiles)));

        return new Screen(sb.ToString(), kb);
    }
}