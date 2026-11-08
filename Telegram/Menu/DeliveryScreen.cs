using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;

namespace WorkGreg.Telegram.Menu;

/// <summary>Instant or daily digest. Applies to all profiles.</summary>
[Handles(CallbackAction.Delivery)]
internal sealed class DeliveryScreen(SettingsService settings, PendingInputStore pending) : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) => RenderAsync(ctx.ChatId, null, ctx.Ct);

    public async Task<Screen> RenderAsync(long chatId, string? notice, CancellationToken ct)
    {
        var user = await settings.GetAsync(chatId, ct);
        var digest = user.Delivery == DeliveryMode.Digest;

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📬 Delivery</b>");
        sb.AppendLine("<i>Applies to all profiles.</i>");
        sb.AppendLine();
        sb.AppendLine($"Now: {DeliveryText(user)}");
        sb.AppendLine();
        sb.AppendLine("⚡ <b>Instant</b>: new matches arrive as soon as a check finds them (hourly).");
        sb.Append("🗓 <b>Daily digest</b>: matches are sent once a day, at the hour you pick (UTC).");

        var rows = new List<InlineKeyboardButton[]>
        {
            Row(Btn((digest ? "" : "✅ ") + "⚡ Instant", CallbackData.DeliveryInstant),
                Btn((digest ? "✅ " : "") + "🗓 Daily digest", CallbackData.DeliveryHourPicker))
        };
        if (digest) rows.Add(Row(Btn($"🕘 Change hour ({user.DigestHourUtc:00}:00 UTC)", CallbackData.DeliveryHourPicker)));
        rows.Add(Row(Btn("⬅ Back", CallbackData.Home)));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }
}

/// <summary>The 24 hour buttons for the daily digest.</summary>
[Handles(CallbackAction.DeliveryHourPicker)]
internal sealed class HourPickerScreen(SettingsService settings, PendingInputStore pending) : NavigationHandler(pending)
{
    protected override async Task<Screen> OpenAsync(CallbackContext ctx)
    {
        var user = await settings.GetAsync(ctx.ChatId, ctx.Ct);
        var rows = new List<InlineKeyboardButton[]>();

        for (var start = 0; start < 24; start += 6)
        {
            rows.Add(Enumerable.Range(start, 6)
                .Select(h => Btn(
                    user.Delivery == DeliveryMode.Digest && user.DigestHourUtc == h ? $"✅{h:00}" : $"{h:00}",
                    CallbackData.DeliveryHour(h)))
                .ToArray());
        }
        rows.Add(Row(Btn("⬅ Back", CallbackData.Delivery)));

        return new Screen(
            "<b>🗓 Daily digest</b>\nPick the hour (UTC) when I should send your matches.",
            new InlineKeyboardMarkup(rows.ToArray()));
    }
}