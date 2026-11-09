namespace WorkGreg.Domain
{
    /// <summary>How a source is fetched. Add new values here, then add an IJobFetcher for them.</summary>
    public enum FetchType
    {
        Feed = 0,     // RSS / Atom
        Html = 1,     // plain HTTP + HTML/JSON parsing (planned)
        Browser = 2,  // headless browser (planned)
        Api = 3       // JSON endpoint; config lives in SourceProvider.Api
    }

    public enum DeliveryMode { Instant = 0, Digest = 1 }
}

namespace WorkGreg.Services
{
    /// <summary>Outcome of one poll cycle.</summary>
    public readonly record struct PollResult(int Sent, bool Stopped);

    /// <summary>What is running right now, seen from one user's point of view.</summary>
    public enum RunStatus
    {
        Idle,
        /// <summary>The hourly all-users cycle. It covers everybody and can't be stopped from chat.</summary>
        Scheduled,
        /// <summary>A manual check started by this user.</summary>
        Yours,
        /// <summary>A manual check started by someone else. This user's own check would wait its turn.</summary>
        Other
    }

    public enum StopOutcome
    {
        Stopped,
        /// <summary>Only the scheduled run is active; users can't stop it.</summary>
        ScheduledRun,
        /// <summary>Nothing is running, or the active run belongs to someone else.</summary>
        NothingOfYours
    }

    public enum AccessMode
    {
        /// <summary>Only the chat ids in <see cref="BotOptions.AllowedChatIds"/> may use the bot (the safe default).</summary>
        Allowlist = 0,

        /// <summary>Any Telegram chat may use the bot.</summary>
        Open = 1
    }

    /// <summary>Shared by the bot (who may talk to it, which token) and the core (token for outgoing messages, API access rule).</summary>
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
}