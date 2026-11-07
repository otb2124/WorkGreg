using WorkGreg.Data;
using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Services;

/// <summary>
/// Use cases for the user-level settings that apply to all profiles (pause, delivery mode, digest hour).
/// Used by the Telegram bot and the HTTP API. A user with no row yet simply has the defaults.
/// </summary>
public sealed class SettingsService(AppDb db)
{
    private const int LastHour = 23;

    /// <summary>The user's settings, or the defaults when nothing was saved yet. Not tracked, not persisted.</summary>
    public async Task<UserProfile> GetAsync(long chatId, CancellationToken ct) =>
        await db.Profiles.AsNoTracking().FirstOrDefaultAsync(s => s.ChatId == chatId, ct)
        ?? new UserProfile { ChatId = chatId };

    public async Task<Result<UserProfile, Error>> SetPausedAsync(long chatId, bool paused, CancellationToken ct)
    {
        var settings = await LoadOrCreateAsync(chatId, ct);
        settings.Paused = paused;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>
    /// Instant ignores <paramref name="digestHourUtc"/>. Digest needs an hour between 0 and 23 (UTC).
    /// </summary>
    public async Task<Result<UserProfile, Error>> SetDeliveryAsync(
        long chatId, DeliveryMode mode, int? digestHourUtc, CancellationToken ct)
    {
        if (mode == DeliveryMode.Digest && digestHourUtc is null or < 0 or > LastHour)
            return Error.Validation($"The digest hour must be between 0 and {LastHour} (UTC).");

        var settings = await LoadOrCreateAsync(chatId, ct);
        settings.Delivery = mode;
        if (mode == DeliveryMode.Digest) settings.DigestHourUtc = digestHourUtc!.Value;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>Replaces all settings at once (the HTTP API sends the whole form). The hour is clamped to 0-23.</summary>
    public async Task<Result<UserProfile, Error>> ReplaceAsync(
        long chatId, bool paused, DeliveryMode delivery, int digestHourUtc, CancellationToken ct)
    {
        var settings = await LoadOrCreateAsync(chatId, ct);
        settings.Paused = paused;
        settings.Delivery = delivery;
        settings.DigestHourUtc = Math.Clamp(digestHourUtc, 0, LastHour);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    private async Task<UserProfile> LoadOrCreateAsync(long chatId, CancellationToken ct)
    {
        var settings = await db.Profiles.FindAsync(new object[] { chatId }, ct);
        if (settings is null)
        {
            settings = new UserProfile { ChatId = chatId };
            db.Profiles.Add(settings);
        }
        return settings;
    }
}