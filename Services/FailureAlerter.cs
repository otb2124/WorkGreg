using Microsoft.EntityFrameworkCore;
using WorkGreg.Data;
using WorkGreg.Domain;

namespace WorkGreg.Services;

/// <summary>
/// Tells a user when something is wrong, without ever breaking the cycle, and reacts when a chat can't be reached.
/// Also the single place for best-effort messages (limit notices go through here too).
/// </summary>
public sealed class FailureAlerter(INotifier notifier, ILogger<FailureAlerter> log)
{
    /// <summary>After this many failures in a row the owner is told once, not every hour.</summary>
    public const int SourceFailureThreshold = 3;

    /// <summary>Call after <c>source.ConsecutiveFailures</c> was increased. Alerts exactly when the threshold is reached.</summary>
    public async Task SourceFailedAsync(
        AppDb db, Source source, SearchProfile profile, HashSet<long> unavailable, CancellationToken ct)
    {
        if (source.ConsecutiveFailures != SourceFailureThreshold) return;

        await NotifyAsync(db, profile.OwnerChatId,
            MessageFormatter.SourceFailure(source.Name, profile.Name, SourceFailureThreshold, source.LastError),
            unavailable, ct);
    }

    /// <summary>Best-effort message (alerts, limit notices). Never throws; an unreachable chat gets its profiles disabled.</summary>
    public async Task NotifyAsync(AppDb db, long chatId, string html, HashSet<long> unavailable, CancellationToken ct)
    {
        try
        {
            await notifier.SendAsync(chatId, html, ct);
        }
        catch (RecipientUnreachableException ex)
        {
            await DisableOwnerAsync(db, chatId, ex.Message, unavailable, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Failed to send message to {ChatId}", chatId);
        }
    }

    /// <summary>
    /// The chat can't receive messages (the user blocked the bot or deleted the chat). Disable its profiles so
    /// we stop fetching and trying every hour. The user can re-enable them from the menu if they come back.
    /// </summary>
    public async Task DisableOwnerAsync(
        AppDb db, long owner, string reason, HashSet<long> unavailable, CancellationToken ct)
    {
        unavailable.Add(owner);

        var disabled = await db.SearchProfiles
            .Where(p => p.OwnerChatId == owner && p.Enabled)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false), ct);

        log.LogWarning("Chat {Owner} can't be reached ({Reason}); disabled {Count} profile(s)", owner, reason, disabled);
    }
}