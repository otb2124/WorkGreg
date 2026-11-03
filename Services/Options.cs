namespace WorkGreg.Services;

public sealed class BotOptions
{
    public string Token { get; set; } = "";

    /// <summary>Telegram chat ids allowed to use the bot. Empty = anyone (not recommended).</summary>
    public long[] AllowedChatIds { get; set; } = [];
}

public sealed class PollOptions
{
    public int IntervalMinutes { get; set; } = 60;
    public int StartupDelaySeconds { get; set; } = 10;
    public int MaxMessagesPerRun { get; set; } = 20;
}

public sealed class ApiOptions
{
    /// <summary>Required. Clients send it in the X-Api-Key header.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Browser origins allowed by CORS. Empty = no cross-origin browser access.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}