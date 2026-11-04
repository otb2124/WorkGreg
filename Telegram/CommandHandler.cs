using System.Net;
using System.Text;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;
using WorkGreg.Services;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Telegram;

/// <summary>
/// Slash commands. Everything that is per job hunt takes the profile id as its first argument,
/// e.g. <c>/include 2 helpdesk, support</c>. Error replies start with <see cref="ErrorPrefix"/>;
/// <see cref="MenuHandler"/> uses that to tell success from failure.
/// </summary>
public sealed class CommandHandler(AppDb db, FetcherRegistry fetchers, PollRunner runner)
{
    public const string ErrorPrefix = "⚠️ ";

    public static bool IsError(string reply) => reply.StartsWith(ErrorPrefix, StringComparison.Ordinal);

    public async Task<string> HandleAsync(long chatId, string text, CancellationToken ct)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var at = cmd.IndexOf('@');
        if (at > 0) cmd = cmd[..at];
        var args = parts.Length > 1 ? parts[1] : "";

        return cmd switch
        {
            "/start" or "/help" => Help(),

            "/profiles" or "/sources" => await ListProfilesAsync(chatId, ct),
            "/profile" or "/filters" => await ShowProfileAsync(chatId, args, ct),
            "/addprofile" => await AddProfileAsync(chatId, args, ct),
            "/removeprofile" => await RemoveProfileAsync(chatId, args, ct),
            "/name" => await RenameAsync(chatId, args, ct),
            "/keyword" => await SetKeywordAsync(chatId, args, ct),
            "/enable" => await SetEnabledAsync(chatId, args, true, ct),
            "/disable" => await SetEnabledAsync(chatId, args, false, ct),

            "/addsource" => await AddSourceAsync(chatId, args, ct),
            "/removesource" => await RemoveSourceAsync(chatId, args, ct),

            "/include" => await EditAsync(chatId, args, "/include &lt;profile&gt; kw1, kw2", SetInclude, ct),
            "/exclude" => await EditAsync(chatId, args, "/exclude &lt;profile&gt; kw1, kw2", SetExclude, ct),
            "/companies" => await EditAsync(chatId, args, "/companies &lt;profile&gt; name1, name2", SetCompanies, ct),
            "/locations" => await EditAsync(chatId, args, "/locations &lt;profile&gt; city1, city2", SetLocations, ct),
            "/salary" => await EditAsync(chatId, args, "/salary &lt;profile&gt; &lt;number&gt;  (or off)", SetSalary, ct),
            "/remote" => await EditAsync(chatId, args, "/remote &lt;profile&gt; on|off", SetRemote, ct),

            "/pause" => await SetPausedAsync(chatId, true, ct),
            "/resume" => await SetPausedAsync(chatId, false, ct),
            "/delivery" => await SetDeliveryAsync(chatId, args, ct),
            "/check" => await CheckNowAsync(chatId, ct),
            "/resend" or "/resetprocessed" => await ResendAsync(chatId, ct),
            _ => "Unknown command. Try /help"
        };
    }

    private string Help()
    {
        var sources = SourceIds();
        return $"""
        <b>Job watcher</b>
        I check your profiles every hour and message you about vacancies that match them.
        Tip: /menu opens the button menu.

        A <b>profile</b> is one job hunt: a keyword, its own filters and its own sources.
        Commands take the profile id (see /profiles).

        <b>Profiles</b>
        /profiles
        /addprofile &lt;keyword&gt;       (e.g. /addprofile full stack dev)
        /profile &lt;id&gt;               (details and filters)
        /name &lt;id&gt; &lt;name&gt;
        /keyword &lt;id&gt; &lt;keyword&gt;
        /enable &lt;id&gt;  /disable &lt;id&gt;
        /removeprofile &lt;id&gt;

        <b>Sources of a profile</b>  ({sources})
        /addsource &lt;id&gt; &lt;source&gt;
        /removesource &lt;id&gt; &lt;source&gt;

        <b>Filters of a profile</b>
        /include &lt;id&gt; kw1, kw2       (match ANY; no list = clear)
        /exclude &lt;id&gt; kw1, kw2       (against title only)
        /companies &lt;id&gt; name1, name2
        /locations &lt;id&gt; city1, city2
        /salary &lt;id&gt; 1000            (jobs without reported salary are dropped; /salary &lt;id&gt; off)
        /remote &lt;id&gt; on|off

        <b>Delivery</b> (all profiles)
        /delivery instant
        /delivery digest 9     (hour in UTC, 0-23)

        <b>Control</b>
        /check  (run now)
        /resend  (mark all stored vacancies unsent; the next /check sends all matches again)
        /pause, /resume
        /stop  (cancel a check that is running right now)
        """;
    }

    private static string SourceIds() => string.Join(", ", SourceCatalog.All.Select(p => p.Id));

    // ------------------------------------------------------------------ profiles

    private async Task<string> ListProfilesAsync(long chatId, CancellationToken ct)
    {
        var profiles = await db.SearchProfiles.Where(p => p.OwnerChatId == chatId).OrderBy(p => p.Id).ToListAsync(ct);
        if (profiles.Count == 0) return "No profiles yet. Create one with /addprofile &lt;keyword&gt;";

        var sources = await db.Sources.Where(s => s.OwnerChatId == chatId).ToListAsync(ct);

        var sb = new StringBuilder();
        foreach (var p in profiles)
        {
            sb.AppendLine($"#{p.Id} <b>{Html(p.Name)}</b> {(p.Enabled ? "▶️" : "⏸ disabled")}");
            sb.AppendLine($"Keyword: {Html(p.Keyword)}");

            var mine = sources.Where(s => s.SearchProfileId == p.Id).OrderBy(s => s.Id).ToList();
            sb.AppendLine(mine.Count == 0
                ? "Sources: <i>none yet</i>, add one with /addsource"
                : "Sources: " + string.Join(", ", mine.Select(SourceLabel)));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string SourceLabel(Source s)
    {
        var icon = s.ConsecutiveFailures > 0 ? "❌" : s.LastSuccessUtc is null ? "⏳" : "✅";
        return $"{icon} {Html(s.Name)}";
    }

    private async Task<string> ShowProfileAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out _)) return Err("Usage: /profile &lt;id&gt;");
        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        var sources = await db.Sources.Where(s => s.SearchProfileId == id).OrderBy(s => s.Id).ToListAsync(ct);

        string Show(string s, string empty) => s.Length > 0 ? Html(s) : $"<i>{empty}</i>";

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(p.Name)}</b> #{p.Id} {(p.Enabled ? "▶️ active" : "⏸ disabled")}");
        sb.AppendLine($"Keyword:    {Html(p.Keyword)}");
        sb.AppendLine($"Sources:    {(sources.Count == 0 ? "<i>none</i>" : string.Join(", ", sources.Select(SourceLabel)))}");
        sb.AppendLine($"Include:    {Show(p.Include, "anything")}");
        sb.AppendLine($"Exclude:    {Show(p.Exclude, "nothing")}");
        sb.AppendLine($"Companies:  {Show(p.Companies, "any")}");
        sb.AppendLine($"Locations:  {Show(p.Locations, "any")}");
        sb.AppendLine($"Salary:     {(p.MinSalary is { } s ? $">= {s}" : "<i>any</i>")}");
        sb.Append($"Remote only: {(p.RemoteOnly ? "yes" : "no")}");
        return sb.ToString();
    }

    private async Task<string> AddProfileAsync(long chatId, string args, CancellationToken ct)
    {
        var keyword = SourceCatalog.NormalizeKeyword(args);
        if (keyword is null)
            return Err($"Usage: /addprofile &lt;keyword&gt;  (1-{SourceCatalog.MaxKeywordLength} characters)\nExample: /addprofile full stack dev");

        var mine = await db.SearchProfiles.Where(p => p.OwnerChatId == chatId).ToListAsync(ct);
        if (mine.Count >= ProfileRules.MaxProfilesPerUser)
            return Err($"Profile limit reached ({ProfileRules.MaxProfilesPerUser}).");

        var profile = new SearchProfile
        {
            OwnerChatId = chatId,
            Keyword = keyword,
            Name = ProfileRules.NameFromKeyword(keyword)
        };
        db.SearchProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        // The menu checks IsError, not this text.
        return $"Added profile #{profile.Id} <b>{Html(profile.Name)}</b>. Next: add sources with /addsource {profile.Id} &lt;source&gt; and set filters.";
    }

    private async Task<string> RemoveProfileAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out _)) return Err("Usage: /removeprofile &lt;id&gt;");
        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        var sourceIds = await db.Sources.Where(s => s.SearchProfileId == id).Select(s => s.Id).ToListAsync(ct);
        db.Jobs.RemoveRange(db.Jobs.Where(j => sourceIds.Contains(j.SourceId)));
        db.Sources.RemoveRange(db.Sources.Where(s => s.SearchProfileId == id));
        db.SearchProfiles.Remove(p);
        await db.SaveChangesAsync(ct);
        return $"Removed profile <b>{Html(p.Name)}</b> with its sources and stored vacancies.";
    }

    private async Task<string> RenameAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest)) return Err("Usage: /name &lt;id&gt; &lt;name&gt;");
        var name = ProfileRules.NormalizeName(rest);
        if (name is null) return Err($"The name must be 1-{ProfileRules.MaxNameLength} characters.");

        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        p.Name = name;
        await db.SaveChangesAsync(ct);
        return $"Profile #{p.Id} is now called <b>{Html(p.Name)}</b>.";
    }

    private async Task<string> SetKeywordAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest)) return Err("Usage: /keyword &lt;id&gt; &lt;new keyword&gt;");
        var keyword = SourceCatalog.NormalizeKeyword(rest);
        if (keyword is null) return Err($"The keyword must be 1-{SourceCatalog.MaxKeywordLength} characters.");

        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        var sources = await db.Sources.Where(s => s.SearchProfileId == id).ToListAsync(ct);
        ProfileRules.ApplyKeyword(p, keyword, sources);
        await db.SaveChangesAsync(ct);

        return $"Profile #{p.Id} <b>{Html(p.Name)}</b> now searches for <b>{Html(keyword)}</b> on all its sources. Already stored vacancies are kept. Run /check to fetch.";
    }

    private async Task<string> SetEnabledAsync(long chatId, string args, bool enabled, CancellationToken ct)
    {
        if (!TryId(args, out var id, out _)) return Err($"Usage: /{(enabled ? "enable" : "disable")} &lt;id&gt;");
        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        p.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        return enabled
            ? $"Profile <b>{Html(p.Name)}</b> is active."
            : $"Profile <b>{Html(p.Name)}</b> is disabled: not fetched, nothing delivered until you /enable {p.Id}.";
    }

    // ------------------------------------------------------------------ sources of a profile

    private async Task<string> AddSourceAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest) || rest.Length == 0)
            return Err($"Usage: /addsource &lt;profile id&gt; &lt;source&gt;\nSources: {SourceIds()}\nExample: /addsource 1 remotive");

        var providerId = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var provider = SourceCatalog.Find(providerId);
        if (provider is null) return Err($"Unknown source '{Html(providerId)}'. Available: {SourceIds()}");

        if (!fetchers.Get(provider.Type).IsAvailable)
            return Err($"{Html(provider.Name)} is planned but not implemented yet.");

        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        var exists = await db.Sources.AnyAsync(s => s.SearchProfileId == id && s.Provider == provider.Id, ct);
        if (exists) return Err($"{Html(provider.Name)} is already a source of this profile.");

        var source = ProfileRules.NewSource(p, provider);
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        return $"Added <b>{Html(provider.Name)}</b> to profile <b>{Html(p.Name)}</b>. Current vacancies that match its filters will be sent too (up to 20 per run). Run /check to do it now.";
    }

    private async Task<string> RemoveSourceAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest) || rest.Length == 0)
            return Err("Usage: /removesource &lt;profile id&gt; &lt;source&gt;");

        var providerId = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.SearchProfileId == id && s.Provider != null && s.Provider.ToLower() == providerId.ToLower(), ct);
        if (source is null) return Err("That source is not part of this profile.");

        db.Jobs.RemoveRange(db.Jobs.Where(j => j.SourceId == source.Id));
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return $"Removed <b>{Html(source.Name)}</b> from profile <b>{Html(p.Name)}</b>. Its stored vacancies are forgotten.";
    }

    // ------------------------------------------------------------------ filters of a profile

    private async Task<string> EditAsync(
        long chatId, string args, string usage, Func<SearchProfile, string, string> apply, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest)) return Err($"Usage: {usage}");
        var p = await FindProfileAsync(chatId, id, ct);
        if (p is null) return Err("No such profile.");

        var reply = apply(p, rest);
        if (!IsError(reply)) await db.SaveChangesAsync(ct);
        return reply;
    }

    private static string SetInclude(SearchProfile p, string rest)
    {
        p.Include = FilterEngine.Normalize(rest);
        return p.Include.Length > 0 ? $"Include: {Html(p.Include)}" : "Include filter cleared.";
    }

    private static string SetExclude(SearchProfile p, string rest)
    {
        p.Exclude = FilterEngine.Normalize(rest);
        return p.Exclude.Length > 0 ? $"Exclude: {Html(p.Exclude)}" : "Exclude filter cleared.";
    }

    private static string SetCompanies(SearchProfile p, string rest)
    {
        p.Companies = FilterEngine.Normalize(rest);
        return p.Companies.Length > 0 ? $"Companies: {Html(p.Companies)}" : "Companies filter cleared.";
    }

    private static string SetLocations(SearchProfile p, string rest)
    {
        p.Locations = FilterEngine.Normalize(rest);
        return p.Locations.Length > 0 ? $"Locations: {Html(p.Locations)}" : "Locations filter cleared.";
    }

    private static string SetSalary(SearchProfile p, string rest)
    {
        var arg = rest.Trim().ToLowerInvariant();
        if (arg is "" or "off" or "clear")
        {
            p.MinSalary = null;
            return "Minimum salary: cleared.";
        }

        if (!int.TryParse(arg, out var salary) || salary <= 0)
            return Err("Usage: /salary &lt;profile&gt; &lt;positive number&gt;  (or off)");

        p.MinSalary = salary;
        return $"Minimum salary: {salary} (jobs without a reported salary are dropped).";
    }

    private static string SetRemote(SearchProfile p, string rest)
    {
        var arg = rest.Trim().ToLowerInvariant();
        if (arg is not ("on" or "off")) return Err("Usage: /remote &lt;profile&gt; on|off");

        p.RemoteOnly = arg == "on";
        return $"Remote only: {arg}";
    }

    // ------------------------------------------------------------------ user-level settings and control

    private async Task<string> SetPausedAsync(long chatId, bool paused, CancellationToken ct)
    {
        var s = await GetSettingsAsync(chatId, ct);
        s.Paused = paused;
        await db.SaveChangesAsync(ct);
        return paused ? "⏸ Paused. New vacancies will wait until /resume." : "▶️ Resumed.";
    }

    private async Task<string> CheckNowAsync(long chatId, CancellationToken ct)
    {
        if (runner.IsRunning) return "A check is already running. Send /stop to cancel it.";

        var sent = await runner.RunAsync(chatId, ct);
        return runner.LastRunStopped
            ? $"⏹ Check stopped. {sent} vacancies were sent before it stopped."
            : $"Check finished. {sent} new vacancies sent.";
    }

    private async Task<string> ResendAsync(long chatId, CancellationToken ct)
    {
        // Delivered must be cleared too: DeliverAsync treats "same Hash already Delivered"
        // as a duplicate, so every job would skip itself.
        var count = await db.Jobs
            .Where(j => j.OwnerChatId == chatId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Processed, false)
                .SetProperty(j => j.Delivered, false), ct);

        return count == 0
            ? "Nothing to reset yet, no vacancies stored."
            : $"Reset {count} stored vacancies. Run /check to send everything that matches your profiles again.";
    }

    private async Task<string> SetDeliveryAsync(long chatId, string args, CancellationToken ct)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var s = await GetSettingsAsync(chatId, ct);

        if (parts.Length == 0)
            return Err("Usage: /delivery instant  |  /delivery digest &lt;0-23 UTC&gt;");

        switch (parts[0])
        {
            case "instant":
                s.Delivery = DeliveryMode.Instant;
                await db.SaveChangesAsync(ct);
                return "Delivery: instant (every poll).";

            case "digest":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var hour) || hour is < 0 or > 23)
                    return Err("Usage: /delivery digest &lt;0-23 UTC&gt;  (e.g. /delivery digest 9)");
                s.Delivery = DeliveryMode.Digest;
                s.DigestHourUtc = hour;
                await db.SaveChangesAsync(ct);
                return $"Delivery: daily digest at {hour:00}:00 UTC.";

            default:
                return Err("Usage: /delivery instant  |  /delivery digest &lt;0-23 UTC&gt;");
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<UserProfile> GetSettingsAsync(long chatId, CancellationToken ct)
    {
        var s = await db.Profiles.FindAsync(new object[] { chatId }, ct);
        if (s is null)
        {
            s = new UserProfile { ChatId = chatId };
            db.Profiles.Add(s);
            await db.SaveChangesAsync(ct);
        }
        return s;
    }

    private Task<SearchProfile?> FindProfileAsync(long chatId, int id, CancellationToken ct) =>
        db.SearchProfiles.FirstOrDefaultAsync(p => p.Id == id && p.OwnerChatId == chatId, ct);

    /// <summary>Splits "&lt;#id&gt; rest of the text". False when the first word is not a number.</summary>
    private static bool TryId(string args, out int id, out string rest)
    {
        id = 0;
        var t = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        rest = t.Length > 1 ? t[1] : "";
        return t.Length > 0 && int.TryParse(t[0].TrimStart('#'), out id);
    }

    private static string Err(string html) => ErrorPrefix + html;
    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");
}
