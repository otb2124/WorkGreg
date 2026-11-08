namespace WorkGreg.Services;

public enum AccessMode
{
    /// <summary>Only the chat ids in <see cref="BotOptions.AllowedChatIds"/> may use the bot (the safe default).</summary>
    Allowlist = 0,

    /// <summary>Any Telegram chat may use the bot.</summary>
    Open = 1
}

public sealed class BotOptions
{
    public string Token { get; set; } = "";

    /// <summary>Who may use the bot. Must be chosen explicitly: an empty allowlist does NOT mean "anyone".</summary>
    public AccessMode Access { get; set; } = AccessMode.Allowlist;

    /// <summary>Telegram chat ids allowed to use the bot when <see cref="Access"/> is Allowlist.</summary>
    public long[] AllowedChatIds { get; set; } = [];

    /// <summary>The one access rule, shared by the bot and the HTTP API.</summary>
    public bool IsAllowed(long chatId) =>
        Access == AccessMode.Open || AllowedChatIds.Contains(chatId);
}

public sealed class PollOptions
{
    public int IntervalMinutes { get; set; } = 60;
    public int StartupDelaySeconds { get; set; } = 10;
    public int MaxMessagesPerRun { get; set; } = 20;

    /// <summary>Pause between two messages to the same run. Keeps us under Telegram's per-chat rate limit.</summary>
    public int SendDelayMilliseconds { get; set; } = 300;

    /// <summary>
    /// How many sources may be fetched at the same time. Requests to the same host are still spaced out
    /// (HostThrottle), so this only overlaps different hosts. 1 = one source at a time.
    /// </summary>
    public int MaxParallelFetches { get; set; } = 4;

    /// <summary>Most vacancies in one digest message. A digest is also cut to fit Telegram's message size; the rest follows in the next digest.</summary>
    public int DigestMaxItems { get; set; } = 30;
}

public sealed class RetentionOptions
{
    /// <summary>Processed vacancies first seen more than this many days ago are deleted. 0 = keep everything.</summary>
    public int JobRetentionDays { get; set; } = 90;

    /// <summary>How often the retention job runs.</summary>
    public int IntervalHours { get; set; } = 24;

    public int StartupDelaySeconds { get; set; } = 60;

    public bool Enabled => JobRetentionDays > 0;

    /// <summary>Anything first seen (or posted) before this is outside the retention window. Null when retention is off.</summary>
    public DateTime? CutoffUtc(DateTime nowUtc) => Enabled ? nowUtc.AddDays(-JobRetentionDays) : null;
}

public sealed class ApiOptions
{
    /// <summary>Required. Clients send it in the X-Api-Key header.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Browser origins allowed by CORS. Empty = no cross-origin browser access.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}