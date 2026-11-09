using System.Globalization;

namespace WorkGreg.Fetching;

public static class DateParsing
{
    /// <summary>
    /// ISO / RFC dates, or a Unix timestamp in seconds or milliseconds (Lever, Arbeitnow, Himalayas).
    /// Always UTC. Null when empty or not understood.
    /// </summary>
    public static DateTime? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && epoch > 100_000_000)
        {
            try
            {
                var offset = epoch > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
                return offset.UtcDateTime;
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
    }
}