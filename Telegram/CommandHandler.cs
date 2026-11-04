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
            "/keyword" => await SetSearchKeywordAsync(chatId, args, ct),
            "/removesource" => await RemoveSourceAsync(chatId, args, ct),
            "/filters" => await ShowFiltersAsync(chatId, ct),
            "/include" => await SetKeywordsAsync(chatId, args, include: true, ct),
            "/exclude" => await SetKeywordsAsync(chatId, args, include: false, ct),
            "/remote" => await SetRemoteAsync(chatId, args, ct),
            "/pause" => await SetPausedAsync(chatId, true, ct),
            "/resume" => await SetPausedAsync(chatId, false, ct),
            "/check" => await CheckNowAsync(chatId, ct),
            "/resend" or "/resetprocessed" => await ResendAsync(chatId, ct),
            "/companies" => await SetCompaniesAsync(chatId, args, ct),
            "/locations" => await SetLocationsAsync(chatId, args, ct),
            "/salary" => await SetSalaryAsync(chatId, args, ct),
            "/delivery" => await SetDeliveryAsync(chatId, args, ct),
            _ => "Unknown command. Try /help"
        };
    }

    private string Help()
    {
        var sources = SourceIds();
        return $"""
        <b>Job watcher</b>
        I check your searches every hour and message you about vacancies that match your filters.
        Tip: /menu opens the button menu.

        <b>Searches</b> (a source + a keyword)
        /addsource &lt;source&gt; &lt;keyword&gt;  (sources: {sources})
        /keyword &lt;id&gt; &lt;new keyword&gt;
        /sources
        /removesource &lt;id&gt;

        <b>Filters</b> (apply to every search)
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
        /resend  (mark all stored vacancies unsent; the next /check sends all matches again)
        /pause, /resume
        """;
    }

    private static string SourceIds() => string.Join(", ", SourceCatalog.All.Select(p => p.Id));

    private async Task<string> ListSourcesAsync(long chatId, CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.OwnerChatId == chatId).OrderBy(s => s.Id).ToListAsync(ct);
        if (sources.Count == 0) return "No searches yet. Add one with /addsource";

        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            var status = s.ConsecutiveFailures > 0
                ? $"❌ failing ({s.ConsecutiveFailures}x): {Html(s.LastError)}"
                : s.LastSuccessUtc is null ? "⏳ not checked yet" : "✅ ok";

            var provider = SourceCatalog.Find(s.Provider);
            if (provider is not null)
            {
                sb.AppendLine($"#{s.Id} <b>{Html(provider.Name)}</b> · {Html(s.Keyword)}");
            }
            else
            {
                // Legacy row added with a raw URL before the source catalog existed.
                sb.AppendLine($"#{s.Id} [{s.Type.ToString().ToLowerInvariant()}] <b>{Html(s.Name)}</b>");
                sb.AppendLine($"{Html(s.Url)}");
            }

            sb.AppendLine(status);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> AddSourceAsync(long chatId, string args, CancellationToken ct)
    {
        var t = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (t.Length < 2)
            return $"Usage: /addsource &lt;source&gt; &lt;keyword&gt;\nSources: {SourceIds()}\nExample: /addsource dou full stack";

        var provider = SourceCatalog.Find(t[0]);
        if (provider is null)
            return $"Unknown source '{Html(t[0])}'. Available: {SourceIds()}";

        var keyword = SourceCatalog.NormalizeKeyword(t[1]);
        if (keyword is null)
            return $"The keyword must be 1-{SourceCatalog.MaxKeywordLength} characters.";

        if (!fetchers.Get(provider.Type).IsAvailable)
            return $"{Html(provider.Name)} is planned but not implemented yet.";

        var mine = await db.Sources.Where(s => s.OwnerChatId == chatId).ToListAsync(ct);
        if (mine.Count >= MaxSourcesPerUser)
            return $"Search limit reached ({MaxSourcesPerUser}).";

        var url = provider.BuildUrl(keyword);
        if (mine.Any(s => s.Provider == provider.Id && string.Equals(s.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
            return "You already have that search.";

        var source = new Source
        {
            OwnerChatId = chatId,
            Type = provider.Type,
            Provider = provider.Id,
            Keyword = keyword,
            Url = url,
            Name = SourceCatalog.DisplayName(provider, keyword)
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        // The menu treats a reply starting with "Added" as success.
        return $"Added #{source.Id} <b>{Html(source.Name)}</b>.\nCurrent vacancies that match your filters will be sent too (up to 20 per run). Run /check to do it now.";
    }

    private async Task<string> SetSearchKeywordAsync(long chatId, string args, CancellationToken ct)
    {
        var t = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (t.Length < 2 || !int.TryParse(t[0].TrimStart('#'), out var id))
            return "Usage: /keyword &lt;id&gt; &lt;new keyword&gt;";

        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.OwnerChatId == chatId, ct);
        if (source is null) return "No such search.";

        var provider = SourceCatalog.Find(source.Provider);
        if (provider is null)
            return "This search was added with a raw URL and has no keyword. Remove it and add it again with /addsource.";

        var keyword = SourceCatalog.NormalizeKeyword(t[1]);
        if (keyword is null)
            return $"The keyword must be 1-{SourceCatalog.MaxKeywordLength} characters.";

        var url = provider.BuildUrl(keyword);
        var sameProvider = await db.Sources
            .Where(s => s.OwnerChatId == chatId && s.Id != id && s.Provider == provider.Id)
            .ToListAsync(ct);
        if (sameProvider.Any(s => string.Equals(s.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
            return "You already have that search.";

        source.Keyword = keyword;
        source.Url = url;
        source.Name = SourceCatalog.DisplayName(provider, keyword);
        source.ConsecutiveFailures = 0;
        source.LastError = null;
        await db.SaveChangesAsync(ct);

        // The menu treats a reply starting with "Search #" as success.
        return $"Search #{source.Id} is now <b>{Html(source.Name)}</b>. Already stored vacancies are kept. Run /check to fetch it.";
    }

    private async Task<string> RemoveSourceAsync(long chatId, string args, CancellationToken ct)
    {
        if (!int.TryParse(args.TrimStart('#'), out var id)) return "Usage: /removesource &lt;id&gt;";

        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.OwnerChatId == chatId, ct);
        if (source is null) return "No such search.";

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
            : $"Reset {count} stored vacancies. Run /check to send everything that matches your filters again.";
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
            return "Usage: /salary &lt;positive number&gt;  (or /salary off)";

        p.MinSalary = salary;
        await db.SaveChangesAsync(ct);
        return $"Minimum salary: {salary} (jobs without a reported salary are dropped).";
    }

    private async Task<string> SetDeliveryAsync(long chatId, string args, CancellationToken ct)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var p = await GetProfileAsync(chatId, ct);

        if (parts.Length == 0)
            return "Usage: /delivery instant  |  /delivery digest &lt;0-23 UTC&gt;";

        switch (parts[0])
        {
            case "instant":
                p.Delivery = DeliveryMode.Instant;
                await db.SaveChangesAsync(ct);
                return "Delivery: instant (every poll).";

            case "digest":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var hour) || hour is < 0 or > 23)
                    return "Usage: /delivery digest &lt;0-23 UTC&gt;  (e.g. /delivery digest 9)";
                p.Delivery = DeliveryMode.Digest;
                p.DigestHourUtc = hour;
                await db.SaveChangesAsync(ct);
                return $"Delivery: daily digest at {hour:00}:00 UTC.";

            default:
                return "Usage: /delivery instant  |  /delivery digest &lt;0-23 UTC&gt;";
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