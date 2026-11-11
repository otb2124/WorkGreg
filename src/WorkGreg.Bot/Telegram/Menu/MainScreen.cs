using System.Text;
using WorkGreg.Client;
using static WorkGreg.Telegram.MenuKit;

namespace WorkGreg.Telegram.Menu;

/// <summary>The main menu: status line and the top-level buttons. Also what /menu, a stray text message and a finished check show.</summary>
[Handles(CallbackAction.Home)]
internal sealed class MainScreen(IWorkGregClient client, PendingInputStore pending)
    : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) => RenderAsync(ctx.ChatId, null, ctx.Ct);

    public async Task<Screen> RenderAsync(long chatId, string? notice, CancellationToken ct)
    {
        var overview = await client.GetOverviewAsync(chatId, ct);   // settings + profiles in one request
        var user = overview.Settings;
        var all = overview.Profiles;
        var failing = all.Sum(p => p.Sources.Count(s => s.ConsecutiveFailures > 0));

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>🧭 WorkGreg · job watcher</b>");
        sb.AppendLine();
        sb.AppendLine($"Status: {(user.Paused ? "⏸ paused" : "▶️ active")}");
        sb.AppendLine($"Profiles: {all.Count} ({all.Count(p => p.Enabled)} active){(failing > 0 ? $"  (❌ {failing} failing sources)" : "")}");
        sb.Append($"Delivery: {DeliveryText(user)}");

        var kb = Kb(
            Row(Btn($"📂 Profiles ({all.Count})", CallbackData.Profiles), Btn("📬 Delivery", CallbackData.Delivery)),
            Row(Btn("🔎 Check now", CallbackData.Check)),
            Row(user.Paused
                ? Btn("▶️ Resume alerts", CallbackData.Pause(false))
                : Btn("⏸ Pause alerts", CallbackData.Pause(true))),
            Row(Btn("🔁 Resend all matches", CallbackData.ResendAsk)),
            Row(Btn("❓ Help", CallbackData.Help)));

        return new Screen(sb.ToString(), kb);
    }
}