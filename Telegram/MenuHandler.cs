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
/// A "search" is a Source row: a hardcoded source from <see cref="SourceCatalog"/> (e.g. DOU.ua) plus a keyword.
///
/// Callback data (max 64 bytes):
///   home | help | flt | src | dlv | chk | pause:0/1 | rem | rst | rstok
///   ask:&lt;kind&gt; | clr:&lt;kind&gt; | cancel:&lt;screen&gt;
///        kinds: inc exc com loc sal add:&lt;sourceId&gt; kw:&lt;searchId&gt;
///        screens: home flt src dlv s:&lt;searchId&gt;
///   addsrc | s:&lt;id&gt; | sdel:&lt;id&gt; | sdelok:&lt;id&gt;
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
        var p = await ProfileAsync(chatId, ct);
        var sources = await db.Sources.Where(s => s.OwnerChatId == chatId).ToListAsync(ct);
        var failing = sources.Count(s => s.ConsecutiveFailures > 0);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>🧭 WorkGreg · job watcher</b>");
        sb.AppendLine();
        sb.AppendLine($"Status: {(p.Paused ? "⏸ paused" : "▶️ active")}");
        sb.AppendLine($"Searches: {sources.Count}{(failing > 0 ? $"  (❌ {failing} failing)" : "")}");
        sb.AppendLine($"Filters: {ActiveFilterCount(p)} active");
        sb.Append($"Delivery: {DeliveryText(p)}");

        var kb = Kb(
            Row(Btn($"📡 Searches ({sources.Count})", "src"), Btn("🎯 Filters", "flt")),
            Row(Btn("📬 Delivery", "dlv"), Btn("🔎 Check now", "chk")),
            Row(p.Paused ? Btn("▶️ Resume alerts", "pause:0") : Btn("⏸ Pause alerts", "pause:1")),
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

            case "flt":
            case "src":
            case "dlv":
                pending.Clear(chatId);
                return await RenderAsync(chatId, parts[0], ct);

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
                        "The next check sends every one that matches your filters again " +
                        "(up to 20 per run, the rest follow on later runs).",
                        kb);
                }

            case "rstok":
                {
                    var reply = await commands.HandleAsync(chatId, "/resend", ct);
                    return await MainAsync(chatId, Ok(reply), ct);
                }

            case "rem":
                {
                    var p = await ProfileAsync(chatId, ct);
                    var on = !p.RemoteOnly;
                    await commands.HandleAsync(chatId, on ? "/remote on" : "/remote off", ct);
                    return (await FiltersAsync(chatId, null, ct)) with { Toast = $"Remote only: {(on ? "on" : "off")}" };
                }

            case "ask":
                pending.Set(chatId, new PendingInput(arg, messageId));
                return await PromptAsync(chatId, arg, null, ct);

            case "clr":
                {
                    pending.Clear(chatId);
                    var cmd = arg switch
                    {
                        "inc" => "/include",
                        "exc" => "/exclude",
                        "com" => "/companies",
                        "loc" => "/locations",
                        "sal" => "/salary off",
                        _ => null
                    };
                    if (cmd is not null) await commands.HandleAsync(chatId, cmd, ct);
                    return (await FiltersAsync(chatId, null, ct)) with { Toast = "Cleared" };
                }

            case "cancel":
                pending.Clear(chatId);
                return await RenderAsync(chatId, arg, ct);

            case "addsrc":
                {
                    // Only sources that can actually be fetched right now.
                    var providers = SourceCatalog.All.Where(p => fetchers.Get(p.Type).IsAvailable).ToList();

                    if (providers.Count == 1)
                    {
                        var kind = $"add:{providers[0].Id}";
                        pending.Set(chatId, new PendingInput(kind, messageId));
                        return await PromptAsync(chatId, kind, null, ct);
                    }

                    var rows = providers.Select(p => Row(Btn(p.Name, $"ask:add:{p.Id}"))).ToList();
                    rows.Add(Row(Btn("⬅ Back", "src")));
                    return new Screen(
                        "<b>➕ New search</b>\nWhich source should I search?",
                        new InlineKeyboardMarkup(rows.ToArray()));
                }

            case "s":
                return await SourceDetailAsync(chatId, arg, null, ct);

            case "sdel":
                {
                    var source = await FindSourceAsync(chatId, arg, ct);
                    if (source is null) return await SourcesAsync(chatId, Warn("No such search."), ct);

                    var kb = Kb(Row(Btn("🗑 Yes, remove", $"sdelok:{source.Id}"), Btn("✖ Cancel", $"s:{source.Id}")));
                    return new Screen(
                        $"<b>Remove {Html(source.Name)}?</b>\n" +
                        "Its stored vacancies are forgotten too, so adding it again sends them again. " +
                        "To just search for something else, use ✏️ Change keyword instead.",
                        kb);
                }

            case "sdelok":
                {
                    var reply = await commands.HandleAsync(chatId, $"/removesource {arg}", ct);
                    return await SourcesAsync(chatId, Ok(reply), ct);
                }

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

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isList = pend.Kind is "inc" or "exc" or "com" or "loc";
        var input = string.Join(isList ? ", " : " ", lines);
        var clear = input is "-" or "clear" or "off";

        string command;
        switch (pend.Kind)
        {
            case "inc": command = $"/include {(clear ? "" : input)}"; break;
            case "exc": command = $"/exclude {(clear ? "" : input)}"; break;
            case "com": command = $"/companies {(clear ? "" : input)}"; break;
            case "loc": command = $"/locations {(clear ? "" : input)}"; break;

            case "sal":
                if (clear) { command = "/salary off"; break; }
                if (!int.TryParse(input, out var salary) || salary <= 0)
                    return new TextResult(pend.MessageId,
                        await PromptAsync(chatId, pend.Kind, Warn("Please send a positive whole number."), ct));
                command = $"/salary {salary}";
                break;

            default:
                if (pend.Kind.StartsWith("add:"))
                {
                    command = $"/addsource {pend.Kind[4..]} {input}";
                    break;
                }
                if (pend.Kind.StartsWith("kw:"))
                {
                    command = $"/keyword {pend.Kind[3..]} {input}";
                    break;
                }
                pending.Clear(chatId);
                return new TextResult(pend.MessageId, await MainAsync(chatId, null, ct));
        }

        var reply = await commands.HandleAsync(chatId, command, ct);

        if (pend.Kind.StartsWith("add:"))
        {
            // CommandHandler answers "Added #…" on success; anything else is a validation error: ask again.
            if (!reply.StartsWith("Added", StringComparison.Ordinal))
                return new TextResult(pend.MessageId, await PromptAsync(chatId, pend.Kind, Warn(reply), ct));

            pending.Clear(chatId);
            return new TextResult(pend.MessageId, await SourcesAsync(chatId, Ok(reply), ct));
        }

        if (pend.Kind.StartsWith("kw:"))
        {
            // CommandHandler answers "Search #…" on success.
            if (!reply.StartsWith("Search #", StringComparison.Ordinal))
                return new TextResult(pend.MessageId, await PromptAsync(chatId, pend.Kind, Warn(reply), ct));

            pending.Clear(chatId);
            return new TextResult(pend.MessageId, await SourceDetailAsync(chatId, pend.Kind[3..], Ok(reply), ct));
        }

        pending.Clear(chatId);
        return new TextResult(pend.MessageId, await FiltersAsync(chatId, Ok(reply), ct));
    }

    // ------------------------------------------------------------------ screens

    private Task<Screen> RenderAsync(long chatId, string screen, CancellationToken ct) => screen switch
    {
        "flt" => FiltersAsync(chatId, null, ct),
        "src" => SourcesAsync(chatId, null, ct),
        "dlv" => DeliveryAsync(chatId, null, ct),
        _ when screen.StartsWith("s:") => SourceDetailAsync(chatId, screen[2..], null, ct),
        _ => MainAsync(chatId, null, ct)
    };

    private async Task<Screen> FiltersAsync(long chatId, string? notice, CancellationToken ct)
    {
        var p = await ProfileAsync(chatId, ct);

        string Show(string s, string empty) => s.Length > 0 ? Html(Short(s)) : $"<i>{empty}</i>";

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>🎯 Filters</b>");
        sb.AppendLine("<i>Apply to every search.</i>");
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
            Row(Btn("🔤 Include", "ask:inc"), Btn("🚫 Exclude", "ask:exc")),
            Row(Btn("🏢 Companies", "ask:com"), Btn("📍 Locations", "ask:loc")),
            Row(Btn("💰 Min salary", "ask:sal"), Btn(p.RemoteOnly ? "🏠 Remote: ON ✅" : "🏠 Remote: OFF", "rem")),
            Row(Btn("⬅ Back", "home")));

        return new Screen(sb.ToString(), kb);
    }

    private async Task<Screen> SourcesAsync(long chatId, string? notice, CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.OwnerChatId == chatId).OrderBy(s => s.Id).ToListAsync(ct);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📡 Searches</b>");
        sb.AppendLine();
        sb.Append(sources.Count == 0
            ? "<i>No searches yet.</i> A search is a source (like DOU.ua) plus a keyword."
            : "Tap a search to see its status, change its keyword or remove it.");

        var rows = sources.Select(s =>
        {
            var icon = s.ConsecutiveFailures > 0 ? "❌" : s.LastSuccessUtc is null ? "⏳" : "✅";
            return Row(Btn($"{icon} {Short(s.Name, 28)}", $"s:{s.Id}"));
        }).ToList();

        rows.Add(Row(Btn("➕ New search", "addsrc")));
        rows.Add(Row(Btn("⬅ Back", "home")));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> SourceDetailAsync(long chatId, string idText, string? notice, CancellationToken ct)
    {
        var s = await FindSourceAsync(chatId, idText, ct);
        if (s is null) return await SourcesAsync(chatId, Warn("No such search."), ct);

        var provider = SourceCatalog.Find(s.Provider);

        var status = s.ConsecutiveFailures > 0
            ? $"❌ failing ({s.ConsecutiveFailures}x): {Html(s.LastError)}"
            : s.LastSuccessUtc is null
                ? "⏳ not checked yet"
                : $"✅ ok · last success {s.LastSuccessUtc:yyyy-MM-dd HH:mm} UTC";

        var sb = new StringBuilder();
        AppendNotice(sb, notice);

        if (provider is not null)
        {
            sb.AppendLine($"<b>{Html(provider.Name)}</b> · {Html(s.Keyword)}  #{s.Id}");
            sb.AppendLine();
            sb.AppendLine($"Source: {Html(provider.Name)}");
            sb.AppendLine($"Keyword: {Html(s.Keyword)}");
        }
        else
        {
            // Legacy row added with a raw URL before the source catalog existed.
            sb.AppendLine($"<b>{Html(s.Name)}</b>  #{s.Id}");
            sb.AppendLine();
            sb.AppendLine("Source: <i>custom feed</i>");
        }

        sb.AppendLine($"URL: {Html(s.Url)}");
        sb.Append($"Status: {status}");

        var rows = new List<InlineKeyboardButton[]>();
        if (provider is not null) rows.Add(Row(Btn("✏️ Change keyword", $"ask:kw:{s.Id}")));
        rows.Add(Row(Btn("🗑 Remove", $"sdel:{s.Id}"), Btn("⬅ Back", "src")));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> DeliveryAsync(long chatId, string? notice, CancellationToken ct)
    {
        var p = await ProfileAsync(chatId, ct);
        var digest = p.Delivery == DeliveryMode.Digest;

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📬 Delivery</b>");
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
        var p = await ProfileAsync(chatId, ct);
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

            I check your searches every hour and message you about vacancies that match your filters.

            1. 📡 <b>Searches</b>: pick a source (like DOU.ua) and a keyword to search for.
            2. 🎯 <b>Filters</b>: keywords, companies, locations, salary, remote. They apply to every search.
            3. 📬 <b>Delivery</b>: instant, or one daily digest.
            4. 🔁 <b>Resend</b>: mark everything as unsent, so the next check sends all matches again.

            A new search's current vacancies are sent too (the ones matching your filters), up to 20 per hourly run.

            Slash commands still work too: /menu /check /sources /filters /pause /resume /resend
            """;
        return new Screen(text, Kb(Row(Btn("⬅ Back", "home"))));
    }

    // ------------------------------------------------------------------ prompts (ask for a value)

    private sealed record Field(string Title, string? Current, string Help, string Back, bool CanClear);

    private static Field Describe(UserProfile p, string kind) => kind switch
    {
        "inc" => new("🔤 Include keywords", p.Include,
            "Send keywords separated by commas, e.g. <code>.NET, C#, Angular</code>.\n" +
            "A vacancy matches if <b>any</b> keyword appears in its title, company, location or description.\n" +
            "Tip: DOU titles are often hyphenated, so add <code>full-stack</code> next to <code>full stack</code>.",
            "flt", true),

        "exc" => new("🚫 Exclude keywords", p.Exclude,
            "Vacancies whose <b>title</b> contains any of these are skipped, e.g. <code>senior, lead, principal</code>.",
            "flt", true),

        "com" => new("🏢 Companies", p.Companies,
            "Only vacancies from these companies, separated by commas.\n" +
            "⚠️ RSS feeds usually don't expose a company, so this can hide everything coming from feed sources.",
            "flt", true),

        "loc" => new("📍 Locations", p.Locations,
            "Only vacancies in these locations, separated by commas.\n" +
            "⚠️ RSS feeds usually don't expose a location, so this can hide everything coming from feed sources.",
            "flt", true),

        "sal" => new("💰 Minimum salary", p.MinSalary?.ToString() ?? "",
            "Send a number, e.g. <code>3000</code>.\n" +
            "⚠️ Vacancies with no reported salary are dropped, and feed sources don't report one yet.",
            "flt", true),

        _ when kind.StartsWith("add:") => DescribeAdd(kind[4..]),

        _ => throw new ArgumentException($"Unknown prompt kind '{kind}'.")
    };

    private static Field DescribeAdd(string providerId)
    {
        var provider = SourceCatalog.Find(providerId);
        if (provider is null)
            return new("➕ New search", null, "That source doesn't exist.", "src", false);

        var examples = string.Join(" · ", provider.KeywordExamples.Select(e => $"<code>{Html(e)}</code>"));
        return new($"➕ New search on {Html(provider.Name)}", null,
            "Send one keyword or phrase to search for.\n" +
            $"Examples: {examples}\n" +
            "Your include/exclude filters still apply on top.",
            "src", false);
    }

    private async Task<Field> DescribeKeywordAsync(long chatId, string idText, CancellationToken ct)
    {
        var s = await FindSourceAsync(chatId, idText, ct);
        var provider = SourceCatalog.Find(s?.Provider);
        if (s is null || provider is null)
            return new("✏️ Change keyword", null, "That search can't be edited.", "src", false);

        return new("✏️ Change keyword", s.Keyword ?? "",
            $"Send the new keyword for {Html(provider.Name)}. Vacancies already found are kept; only what is searched changes.",
            $"s:{s.Id}", false);
    }

    private async Task<Screen> PromptAsync(long chatId, string kind, string? notice, CancellationToken ct)
    {
        var f = kind.StartsWith("kw:")
            ? await DescribeKeywordAsync(chatId, kind[3..], ct)
            : Describe(await ProfileAsync(chatId, ct), kind);

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

    private async Task<UserProfile> ProfileAsync(long chatId, CancellationToken ct) =>
        await db.Profiles.FindAsync(new object[] { chatId }, ct) ?? new UserProfile { ChatId = chatId };

    private async Task<Source?> FindSourceAsync(long chatId, string idText, CancellationToken ct) =>
        int.TryParse(idText, out var id)
            ? await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.OwnerChatId == chatId, ct)
            : null;

    private static int ActiveFilterCount(UserProfile p) =>
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
    private static string Warn(string html) => "⚠️ " + html;
    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Short(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";

    private static InlineKeyboardButton Btn(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);
    private static InlineKeyboardButton[] Row(params InlineKeyboardButton[] buttons) => buttons;
    private static InlineKeyboardMarkup Kb(params InlineKeyboardButton[][] rows) => new(rows);
}