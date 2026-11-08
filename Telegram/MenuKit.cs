using System.Collections.Concurrent;
using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram;

/// <summary>One rendered menu page: HTML text, inline keyboard, and an optional toast shown on the button tap.</summary>
public sealed record Screen(string Html, InlineKeyboardMarkup? Keyboard, string? Toast = null);

/// <summary>Result of a plain-text message that answered a pending prompt: which menu message to refresh, and with what.</summary>
public sealed record TextResult(int MessageId, Screen Screen);

/// <summary>"I asked this chat for a value, the next plain text message is the answer."</summary>
internal sealed record PendingInput(PromptTarget Target, int MessageId);

/// <summary>In-memory only: after a restart the user just taps the button again.</summary>
internal sealed class PendingInputStore
{
    private readonly ConcurrentDictionary<long, PendingInput> _items = new();

    public void Set(long chatId, PendingInput input) => _items[chatId] = input;
    public PendingInput? Get(long chatId) => _items.TryGetValue(chatId, out var p) ? p : null;
    public void Clear(long chatId) => _items.TryRemove(chatId, out _);
}

/// <summary>Small helpers every screen and handler uses.</summary>
internal static class MenuKit
{
    public const string NoSuchProfile = "No such profile.";

    public static InlineKeyboardButton Btn(string text, CallbackData data) =>
        InlineKeyboardButton.WithCallbackData(text, data.Encode());

    public static InlineKeyboardButton[] Row(params InlineKeyboardButton[] buttons) => buttons;
    public static InlineKeyboardMarkup Kb(params InlineKeyboardButton[][] rows) => new(rows);

    public static string Short(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>The line shown above a screen after an action: ✅ with the success text, or ⚠️ with the error.</summary>
    public static string Notice<T>(Result<T, Error> result, Func<T, string> ok) =>
        result.IsSuccess ? Ok(ok(result.Value)) : Fail(result.Error);

    public static void AppendNotice(StringBuilder sb, string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return;
        sb.AppendLine(notice);
        sb.AppendLine();
    }

    public static string DeliveryText(UserSettings p) =>
        p.Delivery == DeliveryMode.Instant ? "⚡ instant" : $"🗓 daily digest at {p.DigestHourUtc:00}:00 UTC";

    public static int ActiveFilterCount(SearchProfile p) =>
        new[]
        {
            p.Include.Count > 0, p.Exclude.Count > 0, p.Companies.Count > 0,
            p.Locations.Count > 0, p.MinSalary is not null, p.RemoteOnly
        }.Count(x => x);

    /// <summary>Loads a profile for a screen. Null when the profile is gone or not yours.</summary>
    public static async Task<ProfileDetails?> TryLoadAsync(this ProfileService profiles, long chatId, int id, CancellationToken ct)
    {
        var result = await profiles.GetAsync(chatId, id, ct);
        return result.IsSuccess ? result.Value : null;
    }
}