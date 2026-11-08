using System.Net;
using System.Text;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;

namespace WorkGreg.Services;

/// <summary>One vacancy going into a digest, with the labels the digest groups it under.</summary>
public readonly record struct DigestItem(Job Job, string SourceName, string ProfileName);

/// <summary>A ready digest message and how many of the offered vacancies fit into it (the first <see cref="Included"/>).</summary>
public readonly record struct DigestPage(string Html, int Included);

/// <summary>Everything the poll cycle says to a user, as Telegram HTML. Pure text: no I/O, no state.</summary>
public static class MessageFormatter
{
    private const int SnippetLength = 350;
    private const int MaxLineLength = 200;

    // Telegram allows 4096 characters per message; stay well below it (the markup counts here, which is conservative).
    private const int MaxDigestLength = 3800;
    private const int DigestTitleReserve = 80;
    private const int DigestFooterReserve = 120;

    public static string Vacancy(
        Job job, string sourceName, string profileName, ProfileCriteria criteria, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(job.Title)}</b>");

        // Who / where
        if (!string.IsNullOrWhiteSpace(job.Company)) sb.AppendLine($"🏢 {Html(job.Company)}");

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Location)) where.Add($"📍 {Html(job.Location)}");
        if (RemoteCriterion.LooksRemote(job)) where.Add("🌐 Remote");
        if (where.Count > 0) sb.AppendLine(string.Join(" · ", where));

        // Terms
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.EmploymentType)) terms.Add($"💼 {Html(job.EmploymentType)}");
        if (job.Salary is { } salary) terms.Add($"💰 {salary:N0}");
        if (terms.Count > 0) sb.AppendLine(string.Join(" · ", terms));

        // Freshness
        sb.AppendLine(job.PostedUtc is { } posted
            ? $"🕒 Posted {Ago(posted, now)} ({posted:yyyy-MM-dd})"
            : $"🕒 First seen {Ago(job.FirstSeenUtc, now)}");

        sb.AppendLine($"🔎 <i>{Html(profileName)} · {Html(sourceName)}</i>");

        // Why it matched
        var lines = criteria.Explain(job);
        if (lines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("✅ <b>Matched</b>");
            foreach (var line in lines)
                sb.AppendLine($"• {Html(Truncate(line, MaxLineLength))}");
        }

        // Description preview
        var snippet = Snippet(job.Description);
        if (snippet.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"📝 {Html(snippet)}");
        }

        sb.AppendLine();
        sb.Append($"<a href=\"{WebUtility.HtmlEncode(job.Url)}\">Open vacancy</a>");
        return sb.ToString();
    }

    /// <summary>
    /// One message for the daily digest: compact lines grouped by profile. Takes the vacancies in the order given
    /// (newest first), as many as fit under <paramref name="maxItems"/> and Telegram's size limit (at least one).
    /// When some are left over, the message says they follow in the next digest.
    /// </summary>
    public static DigestPage Digest(IReadOnlyList<DigestItem> items, int maxItems)
    {
        var included = new List<(DigestItem Item, string Line)>();
        var profilesSeen = new HashSet<string>();
        var length = DigestTitleReserve + DigestFooterReserve;

        foreach (var item in items)
        {
            var line = DigestLine(item.Job);
            var cost = line.Length + 1 +
                       (profilesSeen.Contains(item.ProfileName) ? 0 : DigestProfileHeader(item.ProfileName, 99).Length + 2);

            if (included.Count > 0 && (included.Count >= maxItems || length + cost > MaxDigestLength)) break;

            profilesSeen.Add(item.ProfileName);
            length += cost;
            included.Add((item, line));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"🗓 <b>Digest</b> · {included.Count} new {(included.Count == 1 ? "vacancy" : "vacancies")}");

        // GroupBy keeps the order of first appearance, so the profile with the newest vacancy comes first.
        foreach (var group in included.GroupBy(x => x.Item.ProfileName))
        {
            sb.AppendLine();
            sb.AppendLine(DigestProfileHeader(group.Key, group.Count()));
            foreach (var (_, line) in group) sb.AppendLine(line);
        }

        var more = items.Count - included.Count;
        if (more > 0)
        {
            sb.AppendLine();
            sb.Append($"… {more} more will come in the next digest");
        }

        return new DigestPage(sb.ToString().TrimEnd(), included.Count);
    }

    private static string DigestProfileHeader(string profileName, int count) =>
        $"🔎 <b>{Html(Truncate(profileName, 60))}</b> ({count})";

    private static string DigestLine(Job job)
    {
        var sb = new StringBuilder();
        sb.Append($"• <a href=\"{WebUtility.HtmlEncode(job.Url)}\">{Html(Truncate(job.Title, 120))}</a>");

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Company)) details.Add(Html(Truncate(job.Company, 60)));
        if (!string.IsNullOrWhiteSpace(job.Location)) details.Add($"📍 {Html(Truncate(job.Location, 60))}");
        if (RemoteCriterion.LooksRemote(job)) details.Add("🌐");
        if (job.Salary is { } salary) details.Add($"💰 {salary:N0}");
        if (details.Count > 0) sb.Append(" — ").Append(string.Join(" · ", details));

        return sb.ToString();
    }

    /// <summary>A source failed several times in a row (usually: the site changed or blocked us).</summary>
    public static string SourceFailure(string sourceName, string profileName, int failures, string? lastError) =>
        $"⚠️ <b>{Html(sourceName)}</b> in profile <b>{Html(profileName)}</b> failed {failures} times in a row:\n<code>{Html(lastError)}</code>";

    /// <summary>The per-run message limit was hit while vacancies or sources were still left.</summary>
    public static string LimitReached(int max, int waiting, int skippedSources) =>
        $"… limit of {max} reached for this run" +
        (waiting > 0 ? $", {waiting} more matching vacancies are waiting" : "") +
        (skippedSources > 0 ? $", {skippedSources} sources were not checked yet" : "") +
        ". They will arrive on the next run.";

    /// <summary>A manual check found keyword matches but the profile's filters removed all of them.</summary>
    public static string DroppedByFilters(int count) =>
        $"ℹ️ {count} vacancies matched your keyword but were dropped by your profile filters " +
        "(Include, Exclude, Companies, Locations, Min salary, Remote only). Open 🎯 Filters to check them.";

    public static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Ago(DateTime utc, DateTime now)
    {
        var span = now - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)span.TotalMinutes)} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        return $"{(int)span.TotalDays} days ago";
    }

    private static string Snippet(string? description)
    {
        var text = TextUtil.CleanHtml(description, int.MaxValue);
        return text is null ? "" : Truncate(text, SnippetLength);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}