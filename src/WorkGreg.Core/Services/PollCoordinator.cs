namespace WorkGreg.Services;



/// <summary>
/// Who may run and who may stop. Runs are serialized (one at a time). Every run remembers its owner:
/// a user can stop only the manual check that user started, never somebody else's and never the scheduled run.
/// Knows nothing about fetching or sending.
/// </summary>
public sealed class PollCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private ActiveRun? _active;

    /// <summary>
    /// Waits for the previous run to finish, then makes this one the active run. Dispose the lease to release it.
    /// </summary>
    /// <param name="owner">Null = the scheduled all-users run; otherwise the chat that started a manual check.</param>
    public async Task<PollLease> AcquireAsync(long? owner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var run = new ActiveRun(owner, CancellationTokenSource.CreateLinkedTokenSource(ct));
            lock (_stateLock) { _active = run; }
            return new PollLease(this, run);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>What is running, from the point of view of <paramref name="chatId"/>.</summary>
    public RunStatus StatusFor(long chatId)
    {
        lock (_stateLock)
        {
            if (_active is null) return RunStatus.Idle;
            if (_active.Owner is null) return RunStatus.Scheduled;
            return _active.Owner == chatId ? RunStatus.Yours : RunStatus.Other;
        }
    }

    /// <summary>Cancels the manual check that <paramref name="chatId"/> started. Never touches anybody else's run.</summary>
    public StopOutcome RequestStop(long chatId)
    {
        lock (_stateLock)
        {
            if (_active is null) return StopOutcome.NothingOfYours;
            if (_active.Owner is null) return StopOutcome.ScheduledRun;
            if (_active.Owner != chatId) return StopOutcome.NothingOfYours;

            _active.StopRequested = true;
            _active.Cts.Cancel();
            return StopOutcome.Stopped;
        }
    }

    internal void Release(ActiveRun run)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_active, run)) _active = null;
        }

        // Nobody can cancel it any more: RequestStop only sees the active run, under the same lock.
        run.Cts.Dispose();
        _gate.Release();
    }
}

internal sealed class ActiveRun(long? owner, CancellationTokenSource cts)
{
    public long? Owner { get; } = owner;
    public CancellationTokenSource Cts { get; } = cts;
    public volatile bool StopRequested;
}

/// <summary>Proof that a run is the active one. Dispose it (a <c>using</c>) when the run is over.</summary>
public sealed class PollLease : IDisposable
{
    private readonly PollCoordinator _coordinator;
    private readonly ActiveRun _run;
    private int _released;

    internal PollLease(PollCoordinator coordinator, ActiveRun run)
    {
        _coordinator = coordinator;
        _run = run;
    }

    /// <summary>Cancelled by a stop request, or when the outer token (app shutdown) is cancelled.</summary>
    public CancellationToken Token => _run.Cts.Token;

    /// <summary>True when the owner asked for a stop. The outer token may still be cancelled too; check it separately.</summary>
    public bool StopRequested => _run.StopRequested;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _coordinator.Release(_run);
    }
}