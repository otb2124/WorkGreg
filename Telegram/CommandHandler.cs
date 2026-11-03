using System.Net;
using System.Text;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;
using WorkGreg.Services;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Telegram;

public sealed class CommandHandler(AppDb db, FetcherRegistry fetchers, PollRunner runner)
{
    private const int MaxSourcesPerUser = 20;

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
            "/sources" => await ListSourcesAsync(chatId, ct),
            "/addsource" => await AddSourceAsync(chatId, args, ct),
            "/removesource" => await RemoveSourceAsync(chatId, args, ct),
            "/filters" => await ShowFiltersAsync(chatId, ct),
            "/include" => await SetKeywordsAsync(chatId, args, include: true, ct),
            "/exclude" => await SetKeywordsAsync(chatId, args, include: false, ct),
            "/remote" => await SetRemoteAsync(chatId, args, ct),
            "/pause" => await SetPausedAsync(chatId, true, ct),
            "/resume" => await SetPausedAsync(chatId, false, ct),
            "/check" => await CheckNowAsync(chatId, ct),
            "/companies" => await SetCompaniesAsync(chatId, args, ct),
            "/locations" => await SetLocationsAsync(chatId, args, ct),
            "/salary" => await SetSalaryAsync(chatId, args, ct),
            "/delivery" => await SetDeliveryAsync(chatId, args, ct),
            _ => "Unknown command. Try /help"
        };
    }

    private string Help()
    {
        var types = string.Join(", ", fetchers.All.Where(f => f.IsAvailable)
            .Select(f => f.Type.ToString().ToLowerInvariant()));
        return $"""
        <b>Job watcher</b>
        I check your sources every hour and message you about new vacancies that match your filters.

        <b>Sources</b>
        /addsource &lt;type&gt; &lt;url&gt; [name]  (available types: {types})
        /sources
        /removesource &lt;id&gt;

        <b>Filters</b>
        /include kw1, kw2      (match ANY; no args = clear)
        /exclude kw1, kw2      (against title only; no args = clear)
        /companies name1, name2 (against company name; no args = clear)
        /locations city1, city2 (against location; no args = clear)
        /salary 3000           (jobs without reported salary are dropped)
        /salary off
        /remote on|off
        /filters

        <b>Delivery</b>
        /delivery instant
        /delivery digest 9     (hour in UTC, 0-23)

        <b>Control</b>
        /check  (run now)
        /pause, /resume
        """;
    }

    private async Task<string> ListSourcesAsync(long chatId, CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.OwnerChatId == chatId).OrderBy(s => s.Id).ToListAsync(ct);
        if (sources.Count == 0) return "No sources yet. Add one with /addsource";

        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            var status = s.ConsecutiveFailures > 0
                ? $"❌ failing ({s.ConsecutiveFailures}x): {Html(s.LastError)}"
                : s.LastSuccessUtc is null ? "⏳ not checked yet" : "✅ ok";
            sb.AppendLine($"#{s.Id} [{s.Type.ToString().ToLowerInvariant()}] <b>{Html(s.Name)}</b>");
            sb.AppendLine($"{Html(s.Url)}");
            sb.AppendLine(status);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> AddSourceAsync(long chatId, string args, CancellationToken ct)
    {
        var t = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (t.Length < 2)
            return "Usage: /addsource &lt;type&gt; &lt;url&gt; [name]\nExample: /addsource feed https://example.com/jobs.rss My feed";

        if (!Enum.TryParse<FetchType>(t[0], ignoreCase: true, out var type))
            return $"Unknown type '{Html(t[0])}'. Types: {string.Join(", ", Enum.GetNames<FetchType>().Select(n => n.ToLowerInvariant()))}";

        if (!fetchers.Get(type).IsAvailable)
            return $"Type '{type.ToString().ToLowerInvariant()}' is planned but not implemented yet.";

        if (!Uri.TryCreate(t[1], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "That doesn't look like a valid http(s) URL.";

        if (await db.Sources.CountAsync(s => s.OwnerChatId == chatId, ct) >= MaxSourcesPerUser)
            return $"Source limit reached ({MaxSourcesPerUser}).";

        if (await db.Sources.AnyAsync(s => s.OwnerChatId == chatId && s.Url == uri.ToString(), ct))
            return "You already have that source.";

        var source = new Source
        {
            OwnerChatId = chatId,
            Type = type,
            Url = uri.ToString(),
            Name = t.Length > 2 ? t[2] : uri.Host
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        return $"Added #{source.Id} <b>{Html(source.Name)}</b>.\nThe first check records what's already there; only vacancies posted after that are sent. Run /check to do it now.";
    }

    private async Task<string> RemoveSourceAsync(long chatId, string args, CancellationToken ct)
    {
        if (!int.TryParse(args.TrimStart('#'), out var id)) return "Usage: /removesource &lt;id&gt;";

        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.OwnerChatId == chatId, ct);
        if (source is null) return "No such source.";

        db.Jobs.RemoveRange(db.Jobs.Where(j => j.SourceId == id));
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return $"Removed <b>{Html(source.Name)}</b>.";
    }

    private async Task<string> ShowFiltersAsync(long chatId, CancellationToken ct)
    {
        var p = await GetProfileAsync(chatId, ct);

        string Show(string s, string empty) => s.Length > 0 ? Html(s) : $"<i>{empty}</i>";

        return $"""
        <b>Your filters</b>
        Include:    {Show(p.Include, "anything")}
        Exclude:    {Show(p.Exclude, "nothing")}
        Companies:  {Show(p.Companies, "any")}
        Locations:  {Show(p.Locations, "any")}
        Salary:     {(p.MinSalary is { } s ? $">= {s}" : "<i>any</i>")}
        Remote only: {(p.RemoteOnly ? "yes" : "no")}
        Delivery:   {FormatDelivery(p)}
        Status:     {(p.Paused ? "⏸ paused" : "▶️ active")}
        """;
    }

    private static string FormatDelivery(UserProfile p) =>
        p.Delivery == DeliveryMode.Instant
            ? "instant"
            : $"daily digest at {p.DigestHourUtc:00}:00 UTC";

    private async Task<string> SetKeywordsAsync(long chatId, string args, bool include, CancellationToken ct)
    {
        var p = await GetProfileAsync(chatId, ct);
        var value = FilterEngine.Normalize(args);
        if (include) p.Include = value; else p.Exclude = value;
        await db.SaveChangesAsync(ct);

        var label = include ? "Include" : "Exclude";
        return value.Length > 0 ? $"{label}: {Html(value)}" : $"{label} filter cleared.";
    }

    private async Task<string> SetRemoteAsync(long chatId, string args, CancellationToken ct)
    {
        var arg = args.Trim().ToLowerInvariant();
        if (arg is not ("on" or "off")) return "Usage: /remote on|off";

        var p = await GetProfileAsync(chatId, ct);
        p.RemoteOnly = arg == "on";
        await db.SaveChangesAsync(ct);
        return $"Remote only: {arg}";
    }

    private async Task<string> SetPausedAsync(long chatId, bool paused, CancellationToken ct)
    {
        var p = await GetProfileAsync(chatId, ct);
        p.Paused = paused;
        await db.SaveChangesAsync(ct);
        return paused ? "⏸ Paused. New vacancies will wait until /resume." : "▶️ Resumed.";
    }

    private async Task<string> CheckNowAsync(long chatId, CancellationToken ct)
    {
        var sent = await runner.RunAsync(chatId, ct);
        return $"Check finished. {sent} new vacancies sent.";
    }

    private async Task<UserProfile> GetProfileAsync(long chatId, CancellationToken ct)
    {
        var p = await db.Profiles.FindAsync(new object[] { chatId }, ct);
        if (p is null)
        {
            p = new UserProfile { ChatId = chatId };
            db.Profiles.Add(p);
            await db.SaveChangesAsync(ct);
        }
        return p;
    }

    private async Task<string> SetListFieldAsync(
    long chatId, string args, Func<UserProfile, string> getter, string label, CancellationToken ct)
    {
        // Getter is only used for the "current value" display; we re-resolve the profile below.
        var p = await GetProfileAsync(chatId, ct);
        var value = FilterEngine.Normalize(args);
        if (label == "Companies") p.Companies = value;
        else if (label == "Locations") p.Locations = value;
        else throw new InvalidOperationException(label);

        await db.SaveChangesAsync(ct);
        return value.Length > 0 ? $"{label}: {Html(value)}" : $"{label} filter cleared.";
    }

    private async Task<string> SetSalaryAsync(long chatId, string args, CancellationToken ct)
    {
        var arg = args.Trim().ToLowerInvariant();
        var p = await GetProfileAsync(chatId, ct);

        if (arg is "" or "off" or "clear")
        {
            p.MinSalary = null;
            await db.SaveChangesAsync(ct);
            return "Minimum salary: cleared.";
        }

        if (!int.TryParse(arg, out var salary) || salary <= 0)
            return "Usage: /salary <positive number>  (or /salary off)";

        p.MinSalary = salary;
        await db.SaveChangesAsync(ct);
        return $"Minimum salary: {salary} (jobs without a reported salary are dropped).";
    }

    private async Task<string> SetDeliveryAsync(long chatId, string args, CancellationToken ct)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var p = await GetProfileAsync(chatId, ct);

        if (parts.Length == 0)
            return "Usage: /delivery instant  |  /delivery digest <0-23 UTC>";

        switch (parts[0])
        {
            case "instant":
                p.Delivery = DeliveryMode.Instant;
                await db.SaveChangesAsync(ct);
                return "Delivery: instant (every poll).";

            case "digest":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var hour) || hour is < 0 or > 23)
                    return "Usage: /delivery digest <0-23 UTC>  (e.g. /delivery digest 9)";
                p.Delivery = DeliveryMode.Digest;
                p.DigestHourUtc = hour;
                await db.SaveChangesAsync(ct);
                return $"Delivery: daily digest at {hour:00}:00 UTC.";

            default:
                return "Usage: /delivery instant  |  /delivery digest <0-23 UTC>";
        }
    }

    private async Task<string> SetCompaniesAsync(long chatId, string args, CancellationToken ct)
    {
        var p = await GetProfileAsync(chatId, ct);
        var value = FilterEngine.Normalize(args);
        p.Companies = value;
        await db.SaveChangesAsync(ct);
        return value.Length > 0 ? $"Companies: {Html(value)}" : "Companies filter cleared.";
    }

    private async Task<string> SetLocationsAsync(long chatId, string args, CancellationToken ct)
    {
        var p = await GetProfileAsync(chatId, ct);
        var value = FilterEngine.Normalize(args);
        p.Locations = value;
        await db.SaveChangesAsync(ct);
        return value.Length > 0 ? $"Locations: {Html(value)}" : "Locations filter cleared.";
    }

    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");
}
