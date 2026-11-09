using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace WorkGreg.Services;

/// <summary>How a manual check ended. Failed = it crashed or the app shut down; Sent is then not reliable.</summary>
public sealed record CheckOutcome(int Sent, bool Stopped, bool Failed, DateTime FinishedUtc);

/// <summary>
/// What one user can see about checks: what is running (<see cref="RunStatus"/>), whether their own check is waiting
/// for somebody else's run, and the result of their last check once it is over.
/// </summary>
public sealed record CheckState(RunStatus Status, bool Queued, CheckOutcome? Last);

/// <summary>
/// Lets a front end that cannot hold a request open (the HTTP API) start a user's manual check and look at it later.
/// The run itself is <see cref="PollRunner"/>'s; who may run and stop is <see cref="PollCoordinator"/>'s.
/// This class only starts the run in the background, remembers that it is waiting or done, and keeps the result.
/// In memory: a restart forgets finished results, and a check that was running is gone anyway.
/// </summary>
public sealed class ManualCheckTracker(
    PollRunner runner,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<ManualCheckTracker> log)
{
    private sealed class Pending(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public volatile bool UserStopped;
    }

    // A check that was started and has not ended yet (queued or running), one per chat.
    private readonly ConcurrentDictionary<long, Pending> _pending = new();

    // The last finished check per chat. Cleared when that chat starts the next one.
    private readonly ConcurrentDictionary<long, CheckOutcome> _last = new();

    /// <summary>
    /// Starts a check for this user and returns at once. Busy when this user's own check, or the scheduled hourly
    /// check, is running. (Somebody else's manual check is not an error: this one waits its turn.)
    /// </summary>
    public Result<Unit, Error> Start(long chatId)
    {
        switch (runner.StatusFor(chatId))
        {
            case RunStatus.Yours:
                return Error.Busy("A check is already running.");
            case RunStatus.Scheduled:
                return Error.Busy("The hourly check is running right now.");
        }

        // Linked to app shutdown, so the background run never outlives the host.
        var pending = new Pending(CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping));

        // Also covers two requests arriving together: the second one finds the first one's entry.
        if (!_pending.TryAdd(chatId, pending))
        {
            pending.Cts.Dispose();
            return Error.Busy("A check is already running.");
        }

        _last.TryRemove(chatId, out _);
        _ = Task.Run(() => RunAsync(chatId, pending));
        return Unit.Value;
    }

    public CheckState Get(long chatId)
    {
        var status = runner.StatusFor(chatId);
        _last.TryGetValue(chatId, out var last);

        // Started, not finished, and not the active run: it waits behind somebody else's run.
        var queued = last is null && status != RunStatus.Yours && _pending.ContainsKey(chatId);
        return new CheckState(status, queued, last);
    }

    /// <summary>
    /// Cancels this user's check: the running one, or the one still waiting for its turn.
    /// Never touches anybody else's run and never the scheduled one.
    /// </summary>
    public StopOutcome Stop(long chatId)
    {
        var outcome = runner.RequestStop(chatId);
        if (outcome == StopOutcome.Stopped) return outcome;

        // Not the active run, but maybe waiting for it: cancel the wait.
        if (_pending.TryGetValue(chatId, out var pending))
        {
            pending.UserStopped = true;
            try
            {
                pending.Cts.Cancel();
                return StopOutcome.Stopped;
            }
            catch (ObjectDisposedException)
            {
                // It ended just now; nothing left to stop.
            }
        }

        return outcome;
    }

    private async Task RunAsync(long chatId, Pending pending)
    {
        CheckOutcome outcome;
        try
        {
            var result = await runner.RunAsync(chatId, pending.Cts.Token);
            outcome = new CheckOutcome(result.Sent, result.Stopped, Failed: false, Now());
        }
        catch (OperationCanceledException) when (pending.UserStopped)
        {
            // Stopped while waiting for its turn (a stop of the active run is reported by PollRunner itself).
            outcome = new CheckOutcome(0, Stopped: true, Failed: false, Now());
        }
        catch (Exception ex)
        {
            // The app shutting down also ends up here; only a real crash is worth an error line.
            if (ex is not OperationCanceledException)
                log.LogError(ex, "The manual check of chat {ChatId} failed", chatId);
            outcome = new CheckOutcome(0, Stopped: false, Failed: true, Now());
        }

        // Result first, then the pending entry: a reader never sees "not running" without a result.
        _last[chatId] = outcome;
        _pending.TryRemove(chatId, out _);
        pending.Cts.Dispose();
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}