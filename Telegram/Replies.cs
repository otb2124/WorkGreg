using System.Net;
using WorkGreg.Domain;
using WorkGreg.Services;

namespace WorkGreg.Telegram;

/// <summary>
/// Telegram wording for service results, shared by the slash commands and the button menu.
/// Services return plain data and plain-text errors; this is the only place that turns them into HTML.
/// </summary>
internal static class Replies
{
    public static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Ok(string html) => "✅ " + html;
    public static string Warn(string html) => "⚠️ " + html;
    public static string Usage(string html) => Warn("Usage: " + html);

    /// <summary>A service error as a warning line. Error messages are plain text, so they are encoded here.</summary>
    public static string Fail(Error error) => Warn(Html(error.Message));

    /// <summary>Success text for a slash command, or the warning line for a failure.</summary>
    public static string Render<T>(Result<T, Error> result, Func<T, string> ok) =>
        result.IsSuccess ? ok(result.Value) : Fail(result.Error);

    public static string SourceLabel(Source s)
    {
        var icon = s.ConsecutiveFailures > 0 ? "❌" : s.LastSuccessUtc is null ? "⏳" : "✅";
        return $"{icon} {Html(s.Name)}";
    }

    // ---- profiles

    public static string ProfileAdded(SearchProfile p) =>
        $"Added profile #{p.Id} <b>{Html(p.Name)}</b>. Next: add sources with /addsource {p.Id} &lt;source&gt; and set filters.";

    public static string ProfileRemoved(SearchProfile p) =>
        $"Removed profile <b>{Html(p.Name)}</b> with its sources and stored vacancies.";

    public static string ProfileRenamed(SearchProfile p) =>
        $"Profile #{p.Id} is now called <b>{Html(p.Name)}</b>.";

    public static string KeywordChanged(SearchProfile p) =>
        $"Profile #{p.Id} <b>{Html(p.Name)}</b> now searches for <b>{Html(p.Keyword)}</b> on all its sources. Already stored vacancies are kept. Run /check to fetch.";

    public static string EnabledChanged(SearchProfile p) =>
        p.Enabled
            ? $"Profile <b>{Html(p.Name)}</b> is active."
            : $"Profile <b>{Html(p.Name)}</b> is disabled: not fetched, nothing delivered until you /enable {p.Id}.";

    // ---- sources

    public static string SourceAdded(SourceChange c) =>
        $"Added <b>{Html(c.Source.Name)}</b> to profile <b>{Html(c.Profile.Name)}</b>. Current vacancies that match its filters will be sent too (up to 20 per run). Run /check to do it now.";

    public static string SourceRemoved(SourceChange c) =>
        $"Removed <b>{Html(c.Source.Name)}</b> from profile <b>{Html(c.Profile.Name)}</b>. Its stored vacancies are forgotten.";

    public static string SourceChanged(SourceChange c) => c.Added ? SourceAdded(c) : SourceRemoved(c);

    // ---- filters

    public static string IncludeSet(SearchProfile p) =>
        p.Include.Length > 0 ? $"Include: {Html(p.Include)}" : "Include filter cleared.";

    public static string ExcludeSet(SearchProfile p) =>
        p.Exclude.Length > 0 ? $"Exclude: {Html(p.Exclude)}" : "Exclude filter cleared.";

    public static string CompaniesSet(SearchProfile p) =>
        p.Companies.Length > 0 ? $"Companies: {Html(p.Companies)}" : "Companies filter cleared.";

    public static string LocationsSet(SearchProfile p) =>
        p.Locations.Length > 0 ? $"Locations: {Html(p.Locations)}" : "Locations filter cleared.";

    public static string SalarySet(SearchProfile p) =>
        p.MinSalary is { } salary
            ? $"Minimum salary: {salary} (jobs without a reported salary are dropped)."
            : "Minimum salary: cleared.";

    public static string RemoteSet(SearchProfile p) => $"Remote only: {(p.RemoteOnly ? "on" : "off")}";

    // ---- settings and control

    public static string Paused(UserProfile s) =>
        s.Paused ? "⏸ Paused. New vacancies will wait until /resume." : "▶️ Resumed.";

    public static string Delivery(UserProfile s) =>
        s.Delivery == DeliveryMode.Instant
            ? "Delivery: instant (every poll)."
            : $"Delivery: daily digest at {s.DigestHourUtc:00}:00 UTC.";

    public static string CheckFinished(PollResult r) =>
        r.Stopped
            ? $"⏹ Check stopped. {r.Sent} vacancies were sent before it stopped."
            : $"Check finished. {r.Sent} new vacancies sent.";

    public static string Resent(int count) =>
        count == 0
            ? "Nothing to reset yet, no vacancies stored."
            : $"Reset {count} stored vacancies. Run /check to send everything that matches your profiles again.";
}