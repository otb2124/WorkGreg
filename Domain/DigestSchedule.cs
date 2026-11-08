namespace WorkGreg.Domain;

/// <summary>
/// When a user's daily digest is due. Pure functions of the settings and the clock, so they are easy to test
/// with a fixed "now" (or a FakeTimeProvider).
/// </summary>
public static class DigestSchedule
{
    /// <summary>The most recent digest time (today's, or yesterday's if today's hasn't come yet) at or before <paramref name="nowUtc"/>.</summary>
    public static DateTime LatestSlotUtc(int digestHourUtc, DateTime nowUtc)
    {
        var slot = nowUtc.Date.AddHours(digestHourUtc);
        return slot <= nowUtc ? slot : slot.AddDays(-1);
    }

    /// <summary>
    /// Due = the user is in digest mode and no digest was sent since the latest slot. A missed slot (app down, long
    /// cycle) is made up as soon as the app runs again, and the same slot never sends twice.
    /// </summary>
    public static bool IsDue(UserSettings settings, DateTime nowUtc) =>
        settings.Delivery == DeliveryMode.Digest &&
        (settings.LastDigestSentUtc is not { } last || last < LatestSlotUtc(settings.DigestHourUtc, nowUtc));
}