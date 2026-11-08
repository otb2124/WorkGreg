using WorkGreg.Data;
using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Services;

/// <summary>
/// Use cases for the user-level settings that apply to all profiles (pause, delivery mode, digest hour).
/// Used by the Telegram bot and the HTTP API. A user with no row yet simply has the defaults.
/// </summary>
public sealed class SettingsService(AppDb db, TimeProvider time)
{
    private const int LastHour = 23;

    /// <summary>The user's settings, or the defaults when nothing was saved yet. Not tracked, not persisted.</summary>
    public async Task<UserSettings> GetAsync(long chatId, CancellationToken ct) =>
        await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChatId == chatId, ct)
        ?? new UserSettings { ChatId = chatId };

    public async Task<Result<UserSettings, Error>> SetPausedAsync(long chatId, bool paused, CancellationToken ct)
    {
        var settings = await LoadOrCreateAsync(chatId, ct);
        settings.Paused = paused;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>
    /// Instant ignores <paramref name="digestHourUtc"/>. Digest needs an hour between 0 and 23 (UTC).
    /// </summary>
    public async Task<Result<UserSettings, Error>> SetDeliveryAsync(
        long chatId, DeliveryMode mode, int? digestHourUtc, CancellationToken ct)
    {
        if (mode == DeliveryMode.Digest && digestHourUtc is null or < 0 or > LastHour)
            return Error.Validation($"The digest hour must be between 0 and {LastHour} (UTC).");

        var settings = await LoadOrCreateAsync(chatId, ct);
        var hour = mode == DeliveryMode.Digest ? digestHourUtc!.Value : settings.DigestHourUtc;
        Apply(settings, mode, hour);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>Replaces all settings at once (the HTTP API sends the whole form). The hour is clamped to 0-23.</summary>
    public async Task<Result<UserSettings, Error>> ReplaceAsync(
        long chatId, bool paused, DeliveryMode delivery, int digestHourUtc, CancellationToken ct)
    {
        var settings = await LoadOrCreateAsync(chatId, ct);
        settings.Paused = paused;
        Apply(settings, delivery, Math.Clamp(digestHourUtc, 0, LastHour));
        await db.SaveChangesAsync(ct);
        return settings;
    }

    private void Apply(UserSettings settings, DeliveryMode mode, int digestHourUtc)
    {
        var enteringDigest = mode == DeliveryMode.Digest && settings.Delivery != DeliveryMode.Digest;

        settings.Delivery = mode;
        settings.DigestHourUtc = digestHourUtc;

        // Switching to digest counts as "a digest was just handed over": the first one arrives at the next digest
        // time instead of immediately, whatever the hour is right now.
        if (enteringDigest) settings.LastDigestSentUtc = time.GetUtcNow().UtcDateTime;
    }

    private async Task<UserSettings> LoadOrCreateAsync(long chatId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FindAsync(new object[] { chatId }, ct);
        if (settings is null)
        {
            settings = new UserSettings { ChatId = chatId };
            db.UserSettings.Add(settings);
        }
        return settings;
    }
}