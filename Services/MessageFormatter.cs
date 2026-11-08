using System.Net;
using System.Text;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using WorkGreg.Filtering;

namespace WorkGreg.Services;

/// <summary>Everything the poll cycle says to a user, as Telegram HTML. Pure text: no I/O, no state.</summary>
public static class MessageFormatter
{
    private const int SnippetLength = 350;
    private const int MaxLineLength = 200;

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