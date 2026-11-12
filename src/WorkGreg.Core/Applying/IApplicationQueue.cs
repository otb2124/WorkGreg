namespace WorkGreg.Applying;

public enum EnqueueResult
{
    /// <summary>The application will be run, one at a time, in the order it was queued.</summary>
    Queued,

    /// <summary><c>Applier:Enabled</c> is false: nothing runs a browser, the caller keeps the text preview.</summary>
    Disabled,

    /// <summary>The user already used <c>Applier:MaxPerDay</c> runs today (UTC day).</summary>
    DailyLimitReached
}

/// <summary>
/// How the rest of the app hands an application (a row already in status <c>Queued</c>) to the browser runner.
/// Core only knows this interface; the runner lives in WorkGreg.Applier.
/// </summary>
public interface IApplicationQueue
{
    Task<EnqueueResult> EnqueueAsync(long chatId, int applicationId, CancellationToken ct);
}