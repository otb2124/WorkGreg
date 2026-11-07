using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram;

/// <summary>
/// Slash commands: parses the text, calls a service, renders the result as Telegram HTML.
/// No validation or persistence lives here. Everything that is per job hunt takes the profile id
/// as its first argument, e.g. <c>/include 2 helpdesk, support</c>.
/// </summary>
public sealed class CommandHandler(
    ProfileService profiles,
    SourceService sources,
    SettingsService settings,
    JobService jobs)
{
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
            "/removeprofile" => await ProfileCommandAsync(args, "/removeprofile &lt;id&gt;",
                (id, _) => profiles.RemoveAsync(chatId, id, ct), ProfileRemoved),
            "/name" => await ProfileCommandAsync(args, "/name &lt;id&gt; &lt;name&gt;",
                (id, rest) => profiles.RenameAsync(chatId, id, rest, ct), ProfileRenamed),
            "/keyword" => await ProfileCommandAsync(args, "/keyword &lt;id&gt; &lt;new keyword&gt;",
                (id, rest) => profiles.SetKeywordAsync(chatId, id, rest, ct), KeywordChanged),
            "/enable" => await ProfileCommandAsync(args, "/enable &lt;id&gt;",
                (id, _) => profiles.SetEnabledAsync(chatId, id, true, ct), EnabledChanged),
            "/disable" => await ProfileCommandAsync(args, "/disable &lt;id&gt;",
                (id, _) => profiles.SetEnabledAsync(chatId, id, false, ct), EnabledChanged),

            "/addsource" => await AddSourceAsync(chatId, args, ct),
            "/removesource" => await RemoveSourceAsync(chatId, args, ct),

            "/include" => await ProfileCommandAsync(args, "/include &lt;profile&gt; kw1, kw2",
                (id, rest) => profiles.SetIncludeAsync(chatId, id, rest, ct), IncludeSet),
            "/exclude" => await ProfileCommandAsync(args, "/exclude &lt;profile&gt; kw1, kw2",
                (id, rest) => profiles.SetExcludeAsync(chatId, id, rest, ct), ExcludeSet),
            "/companies" => await ProfileCommandAsync(args, "/companies &lt;profile&gt; name1, name2",
                (id, rest) => profiles.SetCompaniesAsync(chatId, id, rest, ct), CompaniesSet),
            "/locations" => await ProfileCommandAsync(args, "/locations &lt;profile&gt; city1, city2",
                (id, rest) => profiles.SetLocationsAsync(chatId, id, rest, ct), LocationsSet),
            "/salary" => await SalaryAsync(chatId, args, ct),
            "/remote" => await RemoteAsync(chatId, args, ct),

            "/pause" => Render(await settings.SetPausedAsync(chatId, true, ct), Paused),
            "/resume" => Render(await settings.SetPausedAsync(chatId, false, ct), Paused),
            "/delivery" => await DeliveryAsync(chatId, args, ct),
            "/check" => Render(await jobs.CheckNowAsync(chatId, ct), CheckFinished),
            "/resend" or "/resetprocessed" => Render(await jobs.ResendAllAsync(chatId, ct), Resent),
            _ => "Unknown command. Try /help"
        };
    }

    private static string Help()
    {
        var sourceIds = SourceIds();
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

        <b>Sources of a profile</b>  ({sourceIds})
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
        var all = await profiles.ListAsync(chatId, ct);
        if (all.Count == 0) return "No profiles yet. Create one with /addprofile &lt;keyword&gt;";

        var sb = new StringBuilder();
        foreach (var (p, mine) in all)
        {
            sb.AppendLine($"#{p.Id} <b>{Html(p.Name)}</b> {(p.Enabled ? "▶️" : "⏸ disabled")}");
            sb.AppendLine($"Keyword: {Html(p.Keyword)}");
            sb.AppendLine(mine.Count == 0
                ? "Sources: <i>none yet</i>, add one with /addsource"
                : "Sources: " + string.Join(", ", mine.Select(SourceLabel)));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ShowProfileAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out _)) return Usage("/profile &lt;id&gt;");
        return Render(await profiles.GetAsync(chatId, id, ct), ProfileCard);
    }

    private static string ProfileCard(ProfileDetails d)
    {
        var (p, list) = d;
        static string Show(string s, string empty) => s.Length > 0 ? Html(s) : $"<i>{empty}</i>";

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(p.Name)}</b> #{p.Id} {(p.Enabled ? "▶️ active" : "⏸ disabled")}");
        sb.AppendLine($"Keyword:    {Html(p.Keyword)}");
        sb.AppendLine($"Sources:    {(list.Count == 0 ? "<i>none</i>" : string.Join(", ", list.Select(SourceLabel)))}");
        sb.AppendLine($"Include:    {Show(p.Include, "anything")}");
        sb.AppendLine($"Exclude:    {Show(p.Exclude, "nothing")}");
        sb.AppendLine($"Companies:  {Show(p.Companies, "any")}");
        sb.AppendLine($"Locations:  {Show(p.Locations, "any")}");
        sb.AppendLine($"Salary:     {(p.MinSalary is { } minSalary ? $">= {minSalary}" : "<i>any</i>")}");
        sb.Append($"Remote only: {(p.RemoteOnly ? "yes" : "no")}");
        return sb.ToString();
    }

    private async Task<string> AddProfileAsync(long chatId, string args, CancellationToken ct)
    {
        if (args.Length == 0)
            return Usage($"/addprofile &lt;keyword&gt;  (1-{SourceCatalog.MaxKeywordLength} characters)\nExample: /addprofile full stack dev");

        return Render(await profiles.CreateAsync(chatId, new ProfileDraft(args), ct), ProfileAdded);
    }

    // ------------------------------------------------------------------ sources of a profile

    private async Task<string> AddSourceAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest) || rest.Length == 0)
            return Usage($"/addsource &lt;profile id&gt; &lt;source&gt;\nSources: {SourceIds()}\nExample: /addsource 1 remotive");

        return Render(await sources.AddAsync(chatId, id, FirstWord(rest), ct), SourceAdded);
    }

    private async Task<string> RemoveSourceAsync(long chatId, string args, CancellationToken ct)
    {
        if (!TryId(args, out var id, out var rest) || rest.Length == 0)
            return Usage("/removesource &lt;profile id&gt; &lt;source&gt;");

        return Render(await sources.RemoveByProviderAsync(chatId, id, FirstWord(rest), ct), SourceRemoved);
    }

    // ------------------------------------------------------------------ commands with their own argument syntax

    private async Task<string> SalaryAsync(long chatId, string args, CancellationToken ct)
    {
        const string usage = "/salary &lt;profile&gt; &lt;positive number&gt;  (or off)";
        if (!TryId(args, out var id, out var rest)) return Usage(usage);

        var arg = rest.Trim().ToLowerInvariant();
        int? salary = null;
        if (arg is not ("" or "off" or "clear"))
        {
            if (!int.TryParse(arg, out var parsed)) return Usage(usage);
            salary = parsed;
        }

        return Render(await profiles.SetMinSalaryAsync(chatId, id, salary, ct), SalarySet);
    }

    private async Task<string> RemoteAsync(long chatId, string args, CancellationToken ct)
    {
        const string usage = "/remote &lt;profile&gt; on|off";
        if (!TryId(args, out var id, out var rest)) return Usage(usage);

        var arg = rest.Trim().ToLowerInvariant();
        if (arg is not ("on" or "off")) return Usage(usage);

        return Render(await profiles.SetRemoteOnlyAsync(chatId, id, arg == "on", ct), RemoteSet);
    }

    private async Task<string> DeliveryAsync(long chatId, string args, CancellationToken ct)
    {
        const string usage = "/delivery instant  |  /delivery digest &lt;0-23 UTC&gt;";
        var parts = args.Trim().ToLowerInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return Usage(usage);

        switch (parts[0])
        {
            case "instant":
                return Render(await settings.SetDeliveryAsync(chatId, DeliveryMode.Instant, null, ct), Delivery);

            case "digest":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var hour))
                    return Usage("/delivery digest &lt;0-23 UTC&gt;  (e.g. /delivery digest 9)");
                return Render(await settings.SetDeliveryAsync(chatId, DeliveryMode.Digest, hour, ct), Delivery);

            default:
                return Usage(usage);
        }
    }

    // ------------------------------------------------------------------ parsing helpers

    /// <summary>Parses "&lt;id&gt; rest", runs the service call and renders its result.</summary>
    private static async Task<string> ProfileCommandAsync(
        string args, string usage,
        Func<int, string, Task<Result<SearchProfile, Error>>> call,
        Func<SearchProfile, string> ok)
    {
        if (!TryId(args, out var id, out var rest)) return Usage(usage);
        return Render(await call(id, rest), ok);
    }

    /// <summary>Splits "&lt;#id&gt; rest of the text". False when the first word is not a number.</summary>
    private static bool TryId(string args, out int id, out string rest)
    {
        id = 0;
        var t = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        rest = t.Length > 1 ? t[1] : "";
        return t.Length > 0 && int.TryParse(t[0].TrimStart('#'), out id);
    }

    private static string FirstWord(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}