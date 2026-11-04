using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using global::Telegram.Bot.Types.ReplyMarkups;

namespace WorkGreg.Telegram;

/// <summary>One rendered menu page: HTML text, inline keyboard, and an optional toast shown on the button tap.</summary>
public sealed record Screen(string Html, InlineKeyboardMarkup? Keyboard, string? Toast = null);

/// <summary>Result of a plain-text message that answered a pending prompt: which menu message to refresh, and with what.</summary>
public sealed record TextResult(int MessageId, Screen Screen);

/// <summary>"I asked this chat for a value, the next plain text message is the answer."</summary>
public sealed record PendingInput(string Kind, int MessageId);

/// <summary>In-memory only: after a restart the user just taps the button again.</summary>
public sealed class PendingInputStore
{
    private readonly ConcurrentDictionary<long, PendingInput> _items = new();

    public void Set(long chatId, PendingInput input) => _items[chatId] = input;
    public PendingInput? Get(long chatId) => _items.TryGetValue(chatId, out var p) ? p : null;
    public void Clear(long chatId) => _items.TryRemove(chatId, out _);
}

/// <summary>
/// Button-driven UI on top of <see cref="CommandHandler"/>. Every change is applied by calling the
/// existing slash-command logic, so validation lives in exactly one place.
///
/// A "profile" is one job hunt (<see cref="SearchProfile"/>): keyword + filters + sources.
///
/// Callback data (max 64 bytes), &lt;id&gt; is a profile id:
///   home | help | dlv | chk | pause:0/1 | rst | rstok
///   pls                       profiles list
///   np                        new profile (asks for a keyword)
///   p:&lt;id&gt;                  profile page
///   pf:&lt;id&gt; | ps:&lt;id&gt;       its filters page / sources page
///   pst:&lt;id&gt;:&lt;provider&gt;     toggle a source on/off for the profile
///   pen:&lt;id&gt; | prem:&lt;id&gt;    toggle enabled / remote-only
///   pdel:&lt;id&gt; | pdelok:&lt;id&gt;
///   ask:&lt;kind&gt; | clr:&lt;kind&gt; | cancel:&lt;screen&gt;
///        kinds: newp | kw:&lt;id&gt; | nm:&lt;id&gt; | inc:&lt;id&gt; exc:&lt;id&gt; com:&lt;id&gt; loc:&lt;id&gt; sal:&lt;id&gt;
///        screens: home | pls | dlv | p:&lt;id&gt; | pf:&lt;id&gt; | ps:&lt;id&gt;
///   dlvi | dlvd | dlvh:&lt;hour&gt;
/// </summary>
public sealed class MenuHandler(AppDb db, CommandHandler commands, FetcherRegistry fetchers, PendingInputStore pending)
{
    // ------------------------------------------------------------------ public API

    public static bool IsSlow(string data) => data == "chk";

    public static Screen Busy(string html) => new(html, null);

    public void CancelPending(long chatId) => pending.Clear(chatId);

    public async Task<Screen> MainAsync(long chatId, string? notice, CancellationToken ct)
    {
        var settings = await SettingsAsync(chatId, ct);
        var profiles = await db.SearchProfiles.Where(p => p.OwnerChatId == chatId).ToListAsync(ct);
        var sources = await db.Sources.Where(s => s.OwnerChatId == chatId).ToListAsync(ct);
        var failing = sources.Count(s => s.ConsecutiveFailures > 0);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>🧭 WorkGreg · job watcher</b>");
        sb.AppendLine();
        sb.AppendLine($"Status: {(settings.Paused ? "⏸ paused" : "▶️ active")}");
        sb.AppendLine($"Profiles: {profiles.Count} ({profiles.Count(p => p.Enabled)} active){(failing > 0 ? $"  (❌ {failing} failing sources)" : "")}");
        sb.Append($"Delivery: {DeliveryText(settings)}");

        var kb = Kb(
            Row(Btn($"📂 Profiles ({profiles.Count})", "pls"), Btn("📬 Delivery", "dlv")),
            Row(Btn("🔎 Check now", "chk")),
            Row(settings.Paused ? Btn("▶️ Resume alerts", "pause:0") : Btn("⏸ Pause alerts", "pause:1")),
            Row(Btn("🔁 Resend all matches", "rst")),
            Row(Btn("❓ Help", "help")));

        return new Screen(sb.ToString(), kb);
    }

    public async Task<Screen> HandleCallbackAsync(long chatId, int messageId, string data, CancellationToken ct)
    {
        var parts = data.Split(':');
        var arg = parts.Length > 1 ? string.Join(':', parts.Skip(1)) : "";

        switch (parts[0])
        {
            case "home":
                pending.Clear(chatId);
                return await MainAsync(chatId, null, ct);

            case "help":
                pending.Clear(chatId);
                return Help();

            case "pls":
            case "dlv":
                pending.Clear(chatId);
                return await RenderAsync(chatId, parts[0], ct);

            case "p":
            case "pf":
            case "ps":
                pending.Clear(chatId);
                return await RenderAsync(chatId, data, ct);

            case "chk":
                {
                    var reply = await commands.HandleAsync(chatId, "/check", ct);
                    return await MainAsync(chatId, Ok(reply), ct);
                }

            case "pause":
                {
                    var reply = await commands.HandleAsync(chatId, arg == "1" ? "/pause" : "/resume", ct);
                    return await MainAsync(chatId, Ok(reply), ct);
                }

            case "rst":
                {
                    var total = await db.Jobs.CountAsync(j => j.OwnerChatId == chatId, ct);
                    var kb = Kb(Row(Btn("🔁 Yes, reset", "rstok"), Btn("✖ Cancel", "home")));
                    return new Screen(
                        "<b>🔁 Resend all matches?</b>\n" +
                        $"{total} stored vacancies will be marked as not yet sent. " +
                        "The next check sends every one that matches your profiles again " +
                        "(up to 20 per run, the rest follow on later runs).",
                        kb);
                }

            case "rstok":
                {
                    var reply = await commands.HandleAsync(chatId, "/resend", ct);
                    return await MainAsync(chatId, Ok(reply), ct);
                }

            // ---- profiles

            case "np":
                pending.Set(chatId, new PendingInput("newp", messageId));
                return await PromptAsync(chatId, "newp", null, ct);

            case "pen":
                {
                    var p = await FindProfileAsync(chatId, arg, ct);
                    if (p is null) return await ProfilesAsync(chatId, Warn("No such profile."), ct);
                    await commands.HandleAsync(chatId, $"{(p.Enabled ? "/disable" : "/enable")} {p.Id}", ct);
                    return (await ProfileAsync(chatId, arg, null, ct)) with { Toast = p.Enabled ? "Profile disabled" : "Profile enabled" };
                }

            case "prem":
                {
                    var p = await FindProfileAsync(chatId, arg, ct);
                    if (p is null) return await ProfilesAsync(chatId, Warn("No such profile."), ct);
                    var on = !p.RemoteOnly;
                    await commands.HandleAsync(chatId, $"/remote {p.Id} {(on ? "on" : "off")}", ct);
                    return (await FiltersAsync(chatId, arg, null, ct)) with { Toast = $"Remote only: {(on ? "on" : "off")}" };
                }

            case "pst":
                {
                    // arg = "<profileId>:<providerId>[:<region>]": add the source if the profile lacks it, remove it otherwise.
                    var bits = arg.Split(':', 3);
                    var p = await FindProfileAsync(chatId, bits[0], ct);
                    if (p is null || bits.Length < 2) return await ProfilesAsync(chatId, Warn("No such profile."), ct);

                    var providerId = bits[1];
                    var back = bits.Length > 2 ? $"{bits[0]}:{bits[2]}" : bits[0];   // stay on the same region page

                    var has = await db.Sources.AnyAsync(
                        s => s.SearchProfileId == p.Id && s.Provider == providerId, ct);
                    var reply = await commands.HandleAsync(chatId, $"{(has ? "/removesource" : "/addsource")} {p.Id} {providerId}", ct);

                    return CommandHandler.IsError(reply)
                        ? await SourcesAsync(chatId, back, Warn(reply), ct)
                        : (await SourcesAsync(chatId, back, null, ct)) with { Toast = has ? "Source removed" : "Source added" };
                }

            case "pdel":
                {
                    var p = await FindProfileAsync(chatId, arg, ct);
                    if (p is null) return await ProfilesAsync(chatId, Warn("No such profile."), ct);

                    var kb = Kb(Row(Btn("🗑 Yes, remove", $"pdelok:{p.Id}"), Btn("✖ Cancel", $"p:{p.Id}")));
                    return new Screen(
                        $"<b>Remove profile {Html(p.Name)}?</b>\n" +
                        "Its sources and stored vacancies are forgotten too, so creating it again sends them again. " +
                        "To just search for something else, use ✏️ Keyword instead.",
                        kb);
                }

            case "pdelok":
                {
                    var reply = await commands.HandleAsync(chatId, $"/removeprofile {arg}", ct);
                    return await ProfilesAsync(chatId, CommandHandler.IsError(reply) ? Warn(reply) : Ok(reply), ct);
                }

            // ---- prompts

            case "ask":
                pending.Set(chatId, new PendingInput(arg, messageId));
                return await PromptAsync(chatId, arg, null, ct);

            case "clr":
                {
                    pending.Clear(chatId);
                    var bits = arg.Split(':', 2);
                    var cmd = bits[0] switch
                    {
                        "inc" => "/include",
                        "exc" => "/exclude",
                        "com" => "/companies",
                        "loc" => "/locations",
                        "sal" => "/salary",
                        _ => null
                    };
                    var id = bits.Length > 1 ? bits[1] : "";
                    if (cmd is not null) await commands.HandleAsync(chatId, bits[0] == "sal" ? $"{cmd} {id} off" : $"{cmd} {id}", ct);
                    return (await FiltersAsync(chatId, id, null, ct)) with { Toast = "Cleared" };
                }

            case "cancel":
                pending.Clear(chatId);
                return await RenderAsync(chatId, arg, ct);

            // ---- delivery

            case "dlvi":
                {
                    await commands.HandleAsync(chatId, "/delivery instant", ct);
                    return (await DeliveryAsync(chatId, null, ct)) with { Toast = "Delivery: instant" };
                }

            case "dlvd":
                return await HourPickerAsync(chatId, ct);

            case "dlvh":
                {
                    await commands.HandleAsync(chatId, $"/delivery digest {arg}", ct);
                    return (await DeliveryAsync(chatId, null, ct)) with { Toast = $"Digest at {arg.PadLeft(2, '0')}:00 UTC" };
                }

            default:
                return await MainAsync(chatId, null, ct);
        }
    }

    /// <summary>Handles a plain text message. Returns null when nothing was waiting for an answer.</summary>
    public async Task<TextResult?> HandleTextAsync(long chatId, string text, CancellationToken ct)
    {
        var pend = pending.Get(chatId);
        if (pend is null) return null;

        var kindParts = pend.Kind.Split(':', 2);
        var kind = kindParts[0];
        var pid = kindParts.Length > 1 ? kindParts[1] : "";

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isList = kind is "inc" or "exc" or "com" or "loc";
        var input = string.Join(isList ? ", " : " ", lines);
        var clear = input is "-" or "clear" or "off";

        string command;
        switch (kind)
        {
            case "newp": command = $"/addprofile {input}"; break;
            case "kw": command = $"/keyword {pid} {input}"; break;
            case "nm": command = $"/name {pid} {input}"; break;
            case "inc": command = $"/include {pid} {(clear ? "" : input)}"; break;
            case "exc": command = $"/exclude {pid} {(clear ? "" : input)}"; break;
            case "com": command = $"/companies {pid} {(clear ? "" : input)}"; break;
            case "loc": command = $"/locations {pid} {(clear ? "" : input)}"; break;

            case "sal":
                if (clear) { command = $"/salary {pid} off"; break; }
                if (!int.TryParse(input, out var salary) || salary <= 0)
                    return new TextResult(pend.MessageId,
                        await PromptAsync(chatId, pend.Kind, Warn("Please send a positive whole number."), ct));
                command = $"/salary {pid} {salary}";
                break;

            default:
                pending.Clear(chatId);
                return new TextResult(pend.MessageId, await MainAsync(chatId, null, ct));
        }

        var reply = await commands.HandleAsync(chatId, command, ct);

        // A validation error: ask again.
        if (CommandHandler.IsError(reply))
            return new TextResult(pend.MessageId, await PromptAsync(chatId, pend.Kind, Warn(reply), ct));

        pending.Clear(chatId);

        switch (kind)
        {
            case "newp":
                {
                    // The profile just created is the newest one of this user.
                    var created = await db.SearchProfiles
                        .Where(p => p.OwnerChatId == chatId)
                        .OrderByDescending(p => p.Id)
                        .FirstAsync(ct);
                    return new TextResult(pend.MessageId,
                        await ProfileAsync(chatId, created.Id.ToString(), Ok(reply), ct));
                }

            case "kw":
            case "nm":
                return new TextResult(pend.MessageId, await ProfileAsync(chatId, pid, Ok(reply), ct));

            default:
                return new TextResult(pend.MessageId, await FiltersAsync(chatId, pid, Ok(reply), ct));
        }
    }

    // ------------------------------------------------------------------ screens

    private Task<Screen> RenderAsync(long chatId, string screen, CancellationToken ct)
    {
        var bits = screen.Split(':', 2);
        var id = bits.Length > 1 ? bits[1] : "";

        return bits[0] switch
        {
            "pls" => ProfilesAsync(chatId, null, ct),
            "dlv" => DeliveryAsync(chatId, null, ct),
            "p" => ProfileAsync(chatId, id, null, ct),
            "pf" => FiltersAsync(chatId, id, null, ct),
            "ps" => SourcesAsync(chatId, id, null, ct),
            _ => MainAsync(chatId, null, ct)
        };
    }

    private async Task<Screen> ProfilesAsync(long chatId, string? notice, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.Where(p => p.OwnerChatId == chatId).OrderBy(p => p.Id).ToListAsync(ct);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📂 Profiles</b>");
        sb.AppendLine();
        sb.Append(profiles.Count == 0
            ? "<i>No profiles yet.</i> A profile is one job hunt: a keyword (e.g. <code>full stack dev</code>), its own filters and its own sources."
            : "Each profile has its own keyword, filters and sources. Tap one to edit it.");

        var rows = profiles
            .Select(p => Row(Btn($"{(p.Enabled ? "▶️" : "⏸")} {Short(p.Name, 28)}", $"p:{p.Id}")))
            .ToList();

        if (profiles.Count < ProfileRules.MaxProfilesPerUser)
            rows.Add(Row(Btn("➕ New profile", "np")));
        rows.Add(Row(Btn("⬅ Back", "home")));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> ProfileAsync(long chatId, string idText, string? notice, CancellationToken ct)
    {
        var p = await FindProfileAsync(chatId, idText, ct);
        if (p is null) return await ProfilesAsync(chatId, Warn("No such profile."), ct);

        var sources = await db.Sources.Where(s => s.SearchProfileId == p.Id).OrderBy(s => s.Id).ToListAsync(ct);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>{Html(p.Name)}</b>  #{p.Id}");
        sb.AppendLine();
        sb.AppendLine($"Status: {(p.Enabled ? "▶️ active" : "⏸ disabled (not fetched, nothing delivered)")}");
        sb.AppendLine($"🔎 Keyword: {Html(p.Keyword)}");
        sb.AppendLine($"🎯 Filters: {ActiveFilterCount(p)} active");

        if (sources.Count == 0)
        {
            sb.Append("📡 Sources: <i>none yet, tap Sources to add some.</i>");
        }
        else
        {
            sb.AppendLine("📡 Sources:");
            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                var status = s.ConsecutiveFailures > 0
                    ? $"❌ failing ({s.ConsecutiveFailures}x): {Html(Short(s.LastError ?? "", 120))}"
                    : s.LastSuccessUtc is null ? "⏳ not checked yet" : $"✅ ok · {s.LastSuccessUtc:yyyy-MM-dd HH:mm} UTC";
                var line = $"  • {Html(s.Name)}: {status}";
                if (i < sources.Count - 1) sb.AppendLine(line); else sb.Append(line);
            }
        }

        var kb = Kb(
            Row(Btn("✏️ Keyword", $"ask:kw:{p.Id}"), Btn("🏷 Rename", $"ask:nm:{p.Id}")),
            Row(Btn("📡 Sources", $"ps:{p.Id}"), Btn("🎯 Filters", $"pf:{p.Id}")),
            Row(Btn(p.Enabled ? "⏸ Disable" : "▶️ Enable", $"pen:{p.Id}")),
            Row(Btn("🗑 Remove profile", $"pdel:{p.Id}"), Btn("⬅ Back", "pls")));

        return new Screen(sb.ToString(), kb);
    }

    private async Task<Screen> SourcesAsync(long chatId, string idText, string? notice, CancellationToken ct)
    {
        // idText is "<profileId>" (region picker) or "<profileId>:<region>" (sources of that region).
        var idParts = idText.Split(':', 2);
        var regionId = idParts.Length > 1 ? idParts[1] : null;

        var p = await FindProfileAsync(chatId, idParts[0], ct);
        if (p is null) return await ProfilesAsync(chatId, Warn("No such profile."), ct);

        var attached = await db.Sources
            .Where(s => s.SearchProfileId == p.Id)
            .Select(s => s.Provider)
            .ToListAsync(ct);

        bool On(SourceProvider x) => attached.Contains(x.Id, StringComparer.OrdinalIgnoreCase);

        // Only sources that can actually be fetched right now.
        var available = SourceCatalog.All.Where(x => fetchers.Get(x.Type).IsAvailable).ToList();

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
                rows.Add(Row(Btn($"{r.Label} ({inRegion.Count}){(active > 0 ? $" · ✅ {active}" : "")}", $"ps:{p.Id}:{r.Id}")));
            }

            rows.Add(Row(Btn("⬅ Back", $"p:{p.Id}")));
        }
        else
        {
            sb.AppendLine($"<b>📡 {Html(region.Label)} · {Html(p.Name)}</b>");
            sb.AppendLine();
            sb.Append($"Tap a source to switch it on or off. Each one searches for <b>{Html(p.Keyword)}</b>.");

            foreach (var x in available.Where(x => SourceCatalog.RegionsOf(x).Contains(region.Id)))
                rows.Add(Row(Btn($"{(On(x) ? "✅" : "☐")} {x.Name}", $"pst:{p.Id}:{x.Id}:{region.Id}")));

            rows.Add(Row(Btn("⬅ Regions", $"ps:{p.Id}")));
        }

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> FiltersAsync(long chatId, string idText, string? notice, CancellationToken ct)
    {
        var p = await FindProfileAsync(chatId, idText, ct);
        if (p is null) return await ProfilesAsync(chatId, Warn("No such profile."), ct);

        string Show(string s, string empty) => s.Length > 0 ? Html(Short(s)) : $"<i>{empty}</i>";

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
            Row(Btn("🔤 Include", $"ask:inc:{p.Id}"), Btn("🚫 Exclude", $"ask:exc:{p.Id}")),
            Row(Btn("🏢 Companies", $"ask:com:{p.Id}"), Btn("📍 Locations", $"ask:loc:{p.Id}")),
            Row(Btn("💰 Min salary", $"ask:sal:{p.Id}"), Btn(p.RemoteOnly ? "🏠 Remote: ON ✅" : "🏠 Remote: OFF", $"prem:{p.Id}")),
            Row(Btn("⬅ Back", $"p:{p.Id}")));

        return new Screen(sb.ToString(), kb);
    }

    private async Task<Screen> DeliveryAsync(long chatId, string? notice, CancellationToken ct)
    {
        var p = await SettingsAsync(chatId, ct);
        var digest = p.Delivery == DeliveryMode.Digest;

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📬 Delivery</b>");
        sb.AppendLine("<i>Applies to all profiles.</i>");
        sb.AppendLine();
        sb.AppendLine($"Now: {DeliveryText(p)}");
        sb.AppendLine();
        sb.AppendLine("⚡ <b>Instant</b>: new matches arrive as soon as a check finds them (hourly).");
        sb.Append("🗓 <b>Daily digest</b>: matches are sent once a day, at the hour you pick (UTC).");

        var rows = new List<InlineKeyboardButton[]>
        {
            Row(Btn((digest ? "" : "✅ ") + "⚡ Instant", "dlvi"),
                Btn((digest ? "✅ " : "") + "🗓 Daily digest", "dlvd"))
        };
        if (digest) rows.Add(Row(Btn($"🕘 Change hour ({p.DigestHourUtc:00}:00 UTC)", "dlvd")));
        rows.Add(Row(Btn("⬅ Back", "home")));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> HourPickerAsync(long chatId, CancellationToken ct)
    {
        var p = await SettingsAsync(chatId, ct);
        var rows = new List<InlineKeyboardButton[]>();

        for (var start = 0; start < 24; start += 6)
        {
            rows.Add(Enumerable.Range(start, 6)
                .Select(h => Btn(
                    p.Delivery == DeliveryMode.Digest && p.DigestHourUtc == h ? $"✅{h:00}" : $"{h:00}",
                    $"dlvh:{h}"))
                .ToArray());
        }
        rows.Add(Row(Btn("⬅ Back", "dlv")));

        return new Screen(
            "<b>🗓 Daily digest</b>\nPick the hour (UTC) when I should send your matches.",
            new InlineKeyboardMarkup(rows.ToArray()));
    }

    private static Screen Help()
    {
        const string text = """
            <b>❓ Help</b>

            I check your profiles every hour and message you about vacancies that match them.

            A 📂 <b>profile</b> is one job hunt, for example "full stack dev" or "IT support". Each profile has:
            • 🔎 a <b>keyword</b> to search for
            • 📡 its own <b>sources</b> (job boards)
            • 🎯 its own <b>filters</b>: include/exclude words, companies, locations, min salary, remote

            📬 <b>Delivery</b> (instant or one daily digest) and ⏸ pause apply to all profiles.
            🔁 <b>Resend</b> marks everything as unsent, so the next check sends all matches again.

            A new source's current vacancies are sent too (the ones matching the profile's filters), up to 20 per hourly run.
            If the same vacancy turns up in several profiles or boards, you get it once.

            Slash commands still work too: /menu /profiles /check /pause /resume /resend /help
            """;
        return new Screen(text, Kb(Row(Btn("⬅ Back", "home"))));
    }

    // ------------------------------------------------------------------ prompts (ask for a value)

    private sealed record Field(string Title, string? Current, string Help, string Back, bool CanClear);

    private async Task<Field> DescribeAsync(long chatId, string kind, CancellationToken ct)
    {
        var bits = kind.Split(':', 2);
        var k = bits[0];

        if (k == "newp")
            return new("➕ New profile", null,
                "Send the keyword to search for, e.g. <code>full stack dev</code> or <code>it support</code>.\n" +
                "It also becomes the profile's name (you can rename it later). Afterwards you pick its sources and filters.",
                "pls", false);

        var p = bits.Length > 1 ? await FindProfileAsync(chatId, bits[1], ct) : null;
        if (p is null)
            return new("Profile not found", null, "That profile doesn't exist (anymore).", "pls", false);

        var filtersBack = $"pf:{p.Id}";
        var profileBack = $"p:{p.Id}";

        return k switch
        {
            "kw" => new("✏️ Keyword", p.Keyword,
                "Send the new keyword. Every source of this profile will search for it. " +
                "Vacancies already found are kept; only what is searched changes.",
                profileBack, false),

            "nm" => new("🏷 Rename profile", p.Name,
                $"Send a new name (up to {ProfileRules.MaxNameLength} characters).",
                profileBack, false),

            "inc" => new("🔤 Include keywords", p.Include,
                "Send keywords separated by commas, e.g. <code>.NET, C#, Angular</code>.\n" +
                "A vacancy matches if <b>any</b> keyword appears in its title, company, location or description.\n" +
                "Tip: titles are often hyphenated, so add <code>full-stack</code> next to <code>full stack</code>.",
                filtersBack, true),

            "exc" => new("🚫 Exclude keywords", p.Exclude,
                "Vacancies whose <b>title</b> contains any of these are skipped, e.g. <code>senior, lead, principal</code>.",
                filtersBack, true),

            "com" => new("🏢 Companies", p.Companies,
                "Only vacancies from these companies, separated by commas.\n" +
                "⚠️ RSS feeds usually don't expose a company, so this can hide everything coming from feed sources.",
                filtersBack, true),

            "loc" => new("📍 Locations", p.Locations,
                "Only vacancies in these locations, separated by commas, e.g. <code>Zagreb, Europe</code>.\n" +
                "⚠️ RSS feeds usually don't expose a location, so this can hide everything coming from feed sources.",
                filtersBack, true),

            "sal" => new("💰 Minimum salary", p.MinSalary?.ToString() ?? "",
                "Send a number, e.g. <code>1000</code>.\n" +
                "⚠️ Vacancies with no reported salary are dropped, and feed sources don't report one yet.",
                filtersBack, true),

            _ => throw new ArgumentException($"Unknown prompt kind '{kind}'.")
        };
    }

    private async Task<Screen> PromptAsync(long chatId, string kind, string? notice, CancellationToken ct)
    {
        var f = await DescribeAsync(chatId, kind, ct);

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
        if (f.CanClear) row.Add(Btn("🗑 Clear", $"clr:{kind}"));
        row.Add(Btn("✖ Cancel", $"cancel:{f.Back}"));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(new[] { row.ToArray() }));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<UserProfile> SettingsAsync(long chatId, CancellationToken ct) =>
        await db.Profiles.FindAsync(new object[] { chatId }, ct) ?? new UserProfile { ChatId = chatId };

    private async Task<SearchProfile?> FindProfileAsync(long chatId, string idText, CancellationToken ct) =>
        int.TryParse(idText, out var id)
            ? await db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct)
            : null;

    private static int ActiveFilterCount(SearchProfile p) =>
        new[]
        {
            p.Include.Length > 0, p.Exclude.Length > 0, p.Companies.Length > 0,
            p.Locations.Length > 0, p.MinSalary is not null, p.RemoteOnly
        }.Count(x => x);

    private static string DeliveryText(UserProfile p) =>
        p.Delivery == DeliveryMode.Instant ? "⚡ instant" : $"🗓 daily digest at {p.DigestHourUtc:00}:00 UTC";

    private static void AppendNotice(StringBuilder sb, string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return;
        sb.AppendLine(notice);
        sb.AppendLine();
    }

    private static string Ok(string html) => "✅ " + html;
    private static string Warn(string html) => html.StartsWith(CommandHandler.ErrorPrefix, StringComparison.Ordinal) ? html : CommandHandler.ErrorPrefix + html;
    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Short(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";

    private static InlineKeyboardButton Btn(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);
    private static InlineKeyboardButton[] Row(params InlineKeyboardButton[] buttons) => buttons;
    private static InlineKeyboardMarkup Kb(params InlineKeyboardButton[][] rows) => new(rows);
}
