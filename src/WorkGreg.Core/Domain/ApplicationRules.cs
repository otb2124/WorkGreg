namespace WorkGreg.Domain;

/// <summary>
/// The lifecycle of an <see cref="Application"/> as data, so the transition table exists in one place
/// (ApplicationService turns it into compare-and-set updates).
/// AppliedManually and Skipped are not reached by a transition: they are how a tracker row is created (no application -> ...).
/// </summary>
public static class ApplicationRules
{
    private static readonly IReadOnlyDictionary<ApplyStatus, ApplyStatus[]> Allowed = new Dictionary<ApplyStatus, ApplyStatus[]>
    {
        [ApplyStatus.Queued] = [ApplyStatus.Filling, ApplyStatus.Cancelled],
        [ApplyStatus.Filling] = [ApplyStatus.Ready, ApplyStatus.NeedsInput, ApplyStatus.Failed, ApplyStatus.Cancelled],
        [ApplyStatus.NeedsInput] = [ApplyStatus.Queued, ApplyStatus.Cancelled],
        [ApplyStatus.Ready] = [ApplyStatus.Submitting, ApplyStatus.Cancelled],
        [ApplyStatus.Submitting] = [ApplyStatus.Submitted, ApplyStatus.Failed],
    };

    public static bool CanTransition(ApplyStatus from, ApplyStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>The statuses an application may be in for a move to <paramref name="to"/> to be allowed. Empty: nothing may move there.</summary>
    public static ApplyStatus[] SourcesOf(ApplyStatus to) =>
        Allowed.Where(kv => kv.Value.Contains(to)).Select(kv => kv.Key).ToArray();

    /// <summary>Nothing moves on from a terminal status.</summary>
    public static bool IsTerminal(ApplyStatus status) => !Allowed.ContainsKey(status);
}
