using System.Collections.Concurrent;
using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.Replies;

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
/// Button-driven UI. Turns callback data and typed answers into service calls and renders the results as screens.
/// Validation and persistence live in the services, so a failure is a <see cref="Result{T, TError}"/> here,
/// not a text that has to be sniffed.
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
public sealed class MenuHandler(
    ProfileService profiles,
    SourceService sources,
    SettingsService settings,
    JobService jobs,
    PendingInputStore pending)
{
    private const string NoSuchProfile = "No such profile.";

    // ------------------------------------------------------------------ public API

    public static bool IsSlow(string data) => data == "chk";

    public static Screen Busy(string html) => new(html, null);

    public void CancelPending(long chatId) => pending.Clear(chatId);

    public async Task<Screen> MainAsync(long chatId, string? notice, CancellationToken ct)
    {
        var user = await settings.GetAsync(chatId, ct);
        var all = await profiles.ListAsync(chatId, ct);
        var failing = all.Sum(d => d.Sources.Count(s => s.ConsecutiveFailures > 0));

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>🧭 WorkGreg · job watcher</b>");
        sb.AppendLine();
        sb.AppendLine($"Status: {(user.Paused ? "⏸ paused" : "▶️ active")}");
        sb.AppendLine($"Profiles: {all.Count} ({all.Count(d => d.Profile.Enabled)} active){(failing > 0 ? $"  (❌ {failing} failing sources)" : "")}");
        sb.Append($"Delivery: {DeliveryText(user)}");

        var kb = Kb(
            Row(Btn($"📂 Profiles ({all.Count})", "pls"), Btn("📬 Delivery", "dlv")),
            Row(Btn("🔎 Check now", "chk")),
            Row(user.Paused ? Btn("▶️ Resume alerts", "pause:0") : Btn("⏸ Pause alerts", "pause:1")),
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
                return await MainAsync(chatId, Notice(await jobs.CheckNowAsync(chatId, ct), CheckFinished), ct);

            case "pause":
                return await MainAsync(chatId, Notice(await settings.SetPausedAsync(chatId, arg == "1", ct), Paused), ct);

            case "rst":
                {
                    var total = await jobs.CountStoredAsync(chatId, ct);
                    var kb = Kb(Row(Btn("🔁 Yes, reset", "rstok"), Btn("✖ Cancel", "home")));
                    return new Screen(
                        "<b>🔁 Resend all matches?</b>\n" +
                        $"{total} stored vacancies will be marked as not yet sent. " +
                        "The next check sends every one that matches your profiles again " +
                        "(up to 20 per run, the rest follow on later runs).",
                        kb);
                }

            case "rstok":
                return await MainAsync(chatId, Notice(await jobs.ResendAllAsync(chatId, ct), Resent), ct);

            // ---- profiles

            case "np":
                pending.Set(chatId, new PendingInput("newp", messageId));
                return await PromptAsync(chatId, "newp", null, ct);

            case "pen":
                {
                    var d = await LoadAsync(chatId, arg, ct);
                    if (d is null) return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

                    var enable = !d.Profile.Enabled;
                    var result = await profiles.SetEnabledAsync(chatId, d.Profile.Id, enable, ct);
                    if (result.IsFailure) return await ProfilesAsync(chatId, Fail(result.Error), ct);

                    return (await ProfileAsync(chatId, arg, null, ct)) with { Toast = enable ? "Profile enabled" : "Profile disabled" };
                }

            case "prem":
                {
                    var d = await LoadAsync(chatId, arg, ct);
                    if (d is null) return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

                    var on = !d.Profile.RemoteOnly;
                    var result = await profiles.SetRemoteOnlyAsync(chatId, d.Profile.Id, on, ct);
                    if (result.IsFailure) return await FiltersAsync(chatId, arg, Fail(result.Error), ct);

                    return (await FiltersAsync(chatId, arg, null, ct)) with { Toast = $"Remote only: {(on ? "on" : "off")}" };
                }

            case "pst":
                {
                    // arg = "<profileId>:<providerId>[:<region>]": add the source if the profile lacks it, remove it otherwise.
                    var bits = arg.Split(':', 3);
                    if (bits.Length < 2 || !int.TryParse(bits[0], out var profileId))
                        return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

                    var back = bits.Length > 2 ? $"{bits[0]}:{bits[2]}" : bits[0];   // stay on the same region page

                    var result = await sources.ToggleAsync(chatId, profileId, bits[1], ct);
                    if (result.IsFailure)
                        return result.Error.Kind == ErrorKind.NotFound
                            ? await ProfilesAsync(chatId, Fail(result.Error), ct)
                            : await SourcesAsync(chatId, back, Fail(result.Error), ct);

                    return (await SourcesAsync(chatId, back, null, ct)) with { Toast = result.Value.Added ? "Source added" : "Source removed" };
                }

            case "pdel":
                {
                    var d = await LoadAsync(chatId, arg, ct);
                    if (d is null) return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

                    var p = d.Profile;
                    var kb = Kb(Row(Btn("🗑 Yes, remove", $"pdelok:{p.Id}"), Btn("✖ Cancel", $"p:{p.Id}")));
                    return new Screen(
                        $"<b>Remove profile {Html(p.Name)}?</b>\n" +
                        "Its sources and stored vacancies are forgotten too, so creating it again sends them again. " +
                        "To just search for something else, use ✏️ Keyword instead.",
                        kb);
                }

            case "pdelok":
                {
                    var result = int.TryParse(arg, out var id)
                        ? await profiles.RemoveAsync(chatId, id, ct)
                        : Error.NotFound(NoSuchProfile);
                    return await ProfilesAsync(chatId, Notice(result, ProfileRemoved), ct);
                }

            // ---- prompts

            case "ask":
                pending.Set(chatId, new PendingInput(arg, messageId));
                return await PromptAsync(chatId, arg, null, ct);

            case "clr":
                {
                    pending.Clear(chatId);
                    var bits = arg.Split(':', 2);
                    var idText = bits.Length > 1 ? bits[1] : "";

                    // Clearing can only fail when the profile is gone; the filters page then shows "No such profile."
                    if (int.TryParse(idText, out var id))
                    {
                        switch (bits[0])
                        {
                            case "inc": await profiles.SetIncludeAsync(chatId, id, null, ct); break;
                            case "exc": await profiles.SetExcludeAsync(chatId, id, null, ct); break;
                            case "com": await profiles.SetCompaniesAsync(chatId, id, null, ct); break;
                            case "loc": await profiles.SetLocationsAsync(chatId, id, null, ct); break;
                            case "sal": await profiles.SetMinSalaryAsync(chatId, id, null, ct); break;
                        }
                    }

                    return (await FiltersAsync(chatId, idText, null, ct)) with { Toast = "Cleared" };
                }

            case "cancel":
                pending.Clear(chatId);
                return await RenderAsync(chatId, arg, ct);

            // ---- delivery

            case "dlvi":
                {
                    var result = await settings.SetDeliveryAsync(chatId, DeliveryMode.Instant, null, ct);
                    if (result.IsFailure) return await DeliveryAsync(chatId, Fail(result.Error), ct);

                    return (await DeliveryAsync(chatId, null, ct)) with { Toast = "Delivery: instant" };
                }

            case "dlvd":
                return await HourPickerAsync(chatId, ct);

            case "dlvh":
                {
                    if (!int.TryParse(arg, out var hour))
                        return await DeliveryAsync(chatId, Warn("Pick an hour between 0 and 23."), ct);

                    var result = await settings.SetDeliveryAsync(chatId, DeliveryMode.Digest, hour, ct);
                    if (result.IsFailure) return await DeliveryAsync(chatId, Fail(result.Error), ct);

                    return (await DeliveryAsync(chatId, null, ct)) with { Toast = $"Digest at {hour:00}:00 UTC" };
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
        var profileId = kindParts.Length > 1 && int.TryParse(kindParts[1], out var parsed) ? parsed : 0;   // 0 = no such profile
        var idText = kindParts.Length > 1 ? kindParts[1] : "";

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isList = kind is "inc" or "exc" or "com" or "loc";
        var input = string.Join(isList ? ", " : " ", lines);
        var clear = input is "-" or "clear" or "off";
        var csv = clear ? "" : input;

        Result<SearchProfile, Error> result;
        switch (kind)
        {
            case "newp": result = await profiles.CreateAsync(chatId, new ProfileDraft(input), ct); break;
            case "kw": result = await profiles.SetKeywordAsync(chatId, profileId, input, ct); break;
            case "nm": result = await profiles.RenameAsync(chatId, profileId, input, ct); break;
            case "inc": result = await profiles.SetIncludeAsync(chatId, profileId, csv, ct); break;
            case "exc": result = await profiles.SetExcludeAsync(chatId, profileId, csv, ct); break;
            case "com": result = await profiles.SetCompaniesAsync(chatId, profileId, csv, ct); break;
            case "loc": result = await profiles.SetLocationsAsync(chatId, profileId, csv, ct); break;

            case "sal":
                if (clear)
                {
                    result = await profiles.SetMinSalaryAsync(chatId, profileId, null, ct);
                    break;
                }

                if (!int.TryParse(input, out var salary) || salary <= 0)
                    return new TextResult(pend.MessageId,
                        await PromptAsync(chatId, pend.Kind, Warn("Please send a positive whole number."), ct));

                result = await profiles.SetMinSalaryAsync(chatId, profileId, salary, ct);
                break;

            default:
                pending.Clear(chatId);
                return new TextResult(pend.MessageId, await MainAsync(chatId, null, ct));
        }

        // A validation error: ask again.
        if (result.IsFailure)
            return new TextResult(pend.MessageId, await PromptAsync(chatId, pend.Kind, Fail(result.Error), ct));

        pending.Clear(chatId);
        var profile = result.Value;

        return kind switch
        {
            "newp" => new TextResult(pend.MessageId,
                await ProfileAsync(chatId, profile.Id.ToString(), Ok(ProfileAdded(profile)), ct)),
            "kw" => new TextResult(pend.MessageId,
                await ProfileAsync(chatId, idText, Ok(KeywordChanged(profile)), ct)),
            "nm" => new TextResult(pend.MessageId,
                await ProfileAsync(chatId, idText, Ok(ProfileRenamed(profile)), ct)),
            _ => new TextResult(pend.MessageId,
                await FiltersAsync(chatId, idText, Ok(FilterReply(kind, profile)), ct))
        };
    }

    private static string FilterReply(string kind, SearchProfile p) => kind switch
    {
        "inc" => IncludeSet(p),
        "exc" => ExcludeSet(p),
        "com" => CompaniesSet(p),
        "loc" => LocationsSet(p),
        _ => SalarySet(p)
    };

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
        var all = await profiles.ListAsync(chatId, ct);

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine("<b>📂 Profiles</b>");
        sb.AppendLine();
        sb.Append(all.Count == 0
            ? "<i>No profiles yet.</i> A profile is one job hunt: a keyword (e.g. <code>full stack dev</code>), its own filters and its own sources."
            : "Each profile has its own keyword, filters and sources. Tap one to edit it.");

        var rows = all
            .Select(d => Row(Btn($"{(d.Profile.Enabled ? "▶️" : "⏸")} {Short(d.Profile.Name, 28)}", $"p:{d.Profile.Id}")))
            .ToList();

        if (all.Count < ProfileRules.MaxProfilesPerUser)
            rows.Add(Row(Btn("➕ New profile", "np")));
        rows.Add(Row(Btn("⬅ Back", "home")));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> ProfileAsync(long chatId, string idText, string? notice, CancellationToken ct)
    {
        var d = await LoadAsync(chatId, idText, ct);
        if (d is null) return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

        var (p, list) = d;

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>{Html(p.Name)}</b>  #{p.Id}");
        sb.AppendLine();
        sb.AppendLine($"Status: {(p.Enabled ? "▶️ active" : "⏸ disabled (not fetched, nothing delivered)")}");
        sb.AppendLine($"🔎 Keyword: {Html(p.Keyword)}");
        sb.AppendLine($"🎯 Filters: {ActiveFilterCount(p)} active");

        if (list.Count == 0)
        {
            sb.Append("📡 Sources: <i>none yet, tap Sources to add some.</i>");
        }
        else
        {
            sb.AppendLine("📡 Sources:");
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var status = s.ConsecutiveFailures > 0
                    ? $"❌ failing ({s.ConsecutiveFailures}x): {Html(Short(s.LastError ?? "", 120))}"
                    : s.LastSuccessUtc is null ? "⏳ not checked yet" : $"✅ ok · {s.LastSuccessUtc:yyyy-MM-dd HH:mm} UTC";
                var line = $"  • {Html(s.Name)}: {status}";
                if (i < list.Count - 1) sb.AppendLine(line); else sb.Append(line);
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

        var d = await LoadAsync(chatId, idParts[0], ct);
        if (d is null) return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

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
        var d = await LoadAsync(chatId, idText, ct);
        if (d is null) return await ProfilesAsync(chatId, Warn(NoSuchProfile), ct);

        var p = d.Profile;

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
            Row(Btn((digest ? "" : "✅ ") + "⚡ Instant", "dlvi"),
                Btn((digest ? "✅ " : "") + "🗓 Daily digest", "dlvd"))
        };
        if (digest) rows.Add(Row(Btn($"🕘 Change hour ({user.DigestHourUtc:00}:00 UTC)", "dlvd")));
        rows.Add(Row(Btn("⬅ Back", "home")));

        return new Screen(sb.ToString(), new InlineKeyboardMarkup(rows.ToArray()));
    }

    private async Task<Screen> HourPickerAsync(long chatId, CancellationToken ct)
    {
        var user = await settings.GetAsync(chatId, ct);
        var rows = new List<InlineKeyboardButton[]>();

        for (var start = 0; start < 24; start += 6)
        {
            rows.Add(Enumerable.Range(start, 6)
                .Select(h => Btn(
                    user.Delivery == DeliveryMode.Digest && user.DigestHourUtc == h ? $"✅{h:00}" : $"{h:00}",
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

        var d = bits.Length > 1 ? await LoadAsync(chatId, bits[1], ct) : null;
        if (d is null)
            return new("Profile not found", null, "That profile doesn't exist (anymore).", "pls", false);

        var p = d.Profile;
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

    /// <summary>Loads a profile for a screen. Null when the id is not a number, or the profile is gone or not yours.</summary>
    private async Task<ProfileDetails?> LoadAsync(long chatId, string idText, CancellationToken ct)
    {
        if (!int.TryParse(idText, out var id)) return null;

        var result = await profiles.GetAsync(chatId, id, ct);
        return result.IsSuccess ? result.Value : null;
    }

    /// <summary>The line shown above a screen after an action: ✅ with the success text, or ⚠️ with the error.</summary>
    private static string Notice<T>(Result<T, Error> result, Func<T, string> ok) =>
        result.IsSuccess ? Ok(ok(result.Value)) : Fail(result.Error);

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

    private static string Short(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";

    private static InlineKeyboardButton Btn(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);
    private static InlineKeyboardButton[] Row(params InlineKeyboardButton[] buttons) => buttons;
    private static InlineKeyboardMarkup Kb(params InlineKeyboardButton[][] rows) => new(rows);
}