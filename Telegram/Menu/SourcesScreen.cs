using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>The sources of a profile: first a region picker, then the sources of one region with on/off switches.</summary>
[Handles(CallbackAction.Sources)]
internal sealed class SourcesScreen(
    ProfileService profiles,
    SourceService sources,
    ProfilesScreen profilesScreen,
    PendingInputStore pending) : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) =>
        RenderAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Data.Region, null, ctx.Ct);

    /// <param name="regionId">Null shows the region picker.</param>
    public async Task<Screen> RenderAsync(long chatId, int profileId, string? regionId, string? notice, CancellationToken ct)
    {
        var d = await profiles.TryLoadAsync(chatId, profileId, ct);
        if (d is null) return await profilesScreen.RenderAsync(chatId, Warn(NoSuchProfile), ct);

        var p = d.Profile;
        var attached = d.Sources.Select(s => s.Provider).ToList();

        bool On(SourceProvider x) => attached.Contains(x.Id, StringComparer.OrdinalIgnoreCase);

        // Only sources that can actually be fetched right now.
        var available = sources.AvailableProviders();

        var region = SourceCatalog.Regions.FirstOrDefault(r => r.Id == regionId);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);

        var rows = new List<InlineKeyboardButton[]>();

        if (region is null)
        {
            sb.AppendLine($"<b>📡 Sources · {Html(p.Name)}</b>");
            sb.AppendLine();
            sb.Append($"Pick a region, then switch sources on or off. Each source searches for <b>{Html(p.Keyword)}</b>.");

            foreach (var r in SourceCatalog.Regions)
            {
                var inRegion = available.Where(x => SourceCatalog.RegionsOf(x).Contains(r.Id)).ToList();
                if (inRegion.Count == 0) continue;
                var active = inRegion.Count(On);
                rows.Add(Row(Btn($"{r.Label} ({inRegion.Count}){(active > 0 ? $" · ✅ {active}" : "")}", CallbackData.Sources(p.Id, r.Id))));
            }

            rows.Add(Row(Btn("⬅ Back", CallbackData.Profile(p.Id))));
        }
        else
        {
            sb.AppendLine($"<b>📡 {Html(region.Label)} · {Html(p.Name)}</b>");
            sb.AppendLine();
            sb.Append($"Tap a source to switch it on or off. Each one searches for <b>{Html(p.Keyword)}</b>.");

            foreach (var x in available.Where(x => SourceCatalog.RegionsOf(x).Contains(region.Id)))
                rows.Add(Row(Btn($"{(On(x) ? "✅" : "☐")} {x.Name}", CallbackData.ToggleSource(p.Id, x.Id, region.Id))));

            rows.Add(Row(Btn("⬅ Regions", CallbackData.Sources(p.Id))));
        }

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }
}