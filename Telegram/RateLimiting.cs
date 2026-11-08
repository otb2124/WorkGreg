using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace WorkGreg.Telegram;

/// <summary>Bound from <c>Bot:RateLimit</c>. Limits what one chat may send to the bot (messages and button taps together).</summary>
public sealed class RateLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How many requests a chat may send at once (the size of its bucket).</summary>
    public int Burst { get; set; } = 8;

    /// <summary>How fast the bucket refills. 1 = one more request every second, so 60 per minute in the long run.</summary>
    public double RefillPerSecond { get; set; } = 1;

    /// <summary>A limited chat is told once, then ignored silently for this long. Keeps the bot from answering a flood with a flood.</summary>
    public int NoticeIntervalSeconds { get; set; } = 10;
}

public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        var errors = new List<string>();

        if (options.Burst is < 1 or > 100)
            errors.Add("Bot:RateLimit:Burst must be between 1 and 100.");
        if (options.RefillPerSecond is <= 0 or > 30)
            errors.Add("Bot:RateLimit:RefillPerSecond must be greater than 0 and at most 30.");
        if (options.NoticeIntervalSeconds is < 0 or > 3600)
            errors.Add("Bot:RateLimit:NoticeIntervalSeconds must be between 0 and 3600.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

/// <param name="Allowed">False: drop the update.</param>
/// <param name="Notify">True for the first refused request of a burst: tell the user. False: stay quiet.</param>
/// <param name="RetryAfter">How long until the chat has a request available again.</param>
internal readonly record struct RateLimitDecision(bool Allowed, bool Notify, TimeSpan RetryAfter);

/// <summary>
/// One token bucket per chat. A chat starts with <see cref="RateLimitOptions.Burst"/> tokens, every request costs one,
/// and tokens come back at <see cref="RateLimitOptions.RefillPerSecond"/>. A normal user never notices it;
/// a script hammering the bot is cut off before it reaches the database.
///
/// In memory, like <see cref="PendingInputStore"/>: a restart gives everybody a full bucket. A chat that has been idle
/// long enough to be full again is dropped from the dictionary, so an open bot does not collect one entry per chat id forever.
/// Time comes from <see cref="TimeProvider"/>, so tests can move it by hand.
/// </summary>
internal sealed class ChatRateLimiter(IOptions<RateLimitOptions> options, TimeProvider time)
{
    private static readonly RateLimitDecision Pass = new(true, false, TimeSpan.Zero);

    private readonly ConcurrentDictionary<long, Bucket> _buckets = new();
    private long _nextSweep;

    public RateLimitDecision TryAcquire(long chatId)
    {
        var o = options.Value;
        if (!o.Enabled) return Pass;

        var now = time.GetTimestamp();
        Sweep(o, now);

        var bucket = _buckets.GetOrAdd(chatId, _ => new Bucket(o.Burst, now));
        lock (bucket)
        {
            var elapsed = time.GetElapsedTime(bucket.Stamp, now).TotalSeconds;
            bucket.Stamp = now;
            bucket.Tokens = Math.Min(o.Burst, bucket.Tokens + elapsed * o.RefillPerSecond);

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return Pass;
            }

            var notify = now >= bucket.NextNotice;
            if (notify) bucket.NextNotice = now + (long)(o.NoticeIntervalSeconds * (double)time.TimestampFrequency);

            return new RateLimitDecision(false, notify, TimeSpan.FromSeconds((1 - bucket.Tokens) / o.RefillPerSecond));
        }
    }

    /// <summary>At most once a minute: forget chats whose bucket is full again (a new bucket would be identical).</summary>
    private void Sweep(RateLimitOptions o, long now)
    {
        var due = Volatile.Read(ref _nextSweep);
        if (now < due || Interlocked.CompareExchange(ref _nextSweep, now + 60 * time.TimestampFrequency, due) != due) return;

        var fullAfterSeconds = o.Burst / o.RefillPerSecond;
        foreach (var (chatId, bucket) in _buckets)
        {
            lock (bucket)
            {
                if (now >= bucket.NextNotice && time.GetElapsedTime(bucket.Stamp, now).TotalSeconds >= fullAfterSeconds)
                    _buckets.TryRemove(new KeyValuePair<long, Bucket>(chatId, bucket));
            }
        }
    }

    private sealed class Bucket(double tokens, long stamp)
    {
        public double Tokens = tokens;
        public long Stamp = stamp;
        public long NextNotice;
    }
}