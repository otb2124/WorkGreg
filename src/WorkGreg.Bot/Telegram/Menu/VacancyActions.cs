using System.Text;
using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using WorkGreg.Services;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

// The three buttons under a delivered vacancy (🤖 AutoFill, ✅ Applied, ⏭ Skip). The core puts them there (see DeliveryCallbacks);
// each carries the vacancy's delivery id, and the core refuses a delivery that is not this chat's.
// The tapped message is the VACANCY, not a menu page, so nothing here replaces its text: AutoFill answers with a new message,
// Applied / Skip only change the buttons under it.

/// <summary>
/// 🤖 AutoFill under a vacancy (and <c>/af_&lt;id&gt;</c> from a digest). Until the browser exists this answers with a text
/// preview of what would be entered, what is missing, and a warning when the vacancy was already tracked.
/// </summary>
[Handles(CallbackAction.AutofillStart)]
internal sealed class AutofillStartHandler(IWorkGregClient client) : ICallbackHandler
{
    private const int ValuePreviewLength = 60;

    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var screen = await RenderAsync(ctx.ChatId, ctx.Data.DeliveryId, ctx.Ct);

        await ctx.Io.AnswerCallbackAsync(ctx.QueryId, null, ctx.Ct);
        await ctx.Io.SendScreenAsync(ctx.ChatId, screen, ctx.Ct);   // a new message: the vacancy message stays as it is
        return CallbackResult.Handled;
    }

    /// <summary>The preview as a screen (also used by the /af command). A failure is a screen with the reason and no buttons.</summary>
    public async Task<Screen> RenderAsync(long chatId, int deliveryId, CancellationToken ct)
    {
        var result = await client.StartAutofillAsync(chatId, deliveryId, ct);
        return result.IsFailure ? new Screen(Fail(result.Error), null) : Render(result.Value);
    }

    private static Screen Render(AutofillPreviewDto p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>🤖 AutoFill</b>");
        sb.AppendLine($"<b>{Html(p.Title)}</b>");
        if (!string.IsNullOrWhiteSpace(p.Company)) sb.AppendLine($"🏢 {Html(p.Company)}");
        sb.AppendLine($"🔎 <i>{Html(p.ProfileName)}</i>");
        sb.AppendLine();

        if (p.Existing is { } existing)
        {
            sb.AppendLine(
                $"⚠️ You already have this vacancy as <b>{Html(ApplyStatusLabels.Of(existing.Status))}</b> " +
                $"({existing.UpdatedUtc:yyyy-MM-dd}). It may be the same job on another board.");
            sb.AppendLine();
        }

        var settings = Kb(Row(Btn("🧩 AutoFill settings", CallbackData.Autofill(p.ProfileId))));

        if (!p.Ready)
        {
            sb.Append("This profile's AutoFill settings are incomplete: first name, last name and email are needed. " +
                      "Open the settings to fill them in.");
            return new Screen(sb.ToString(), settings);
        }

        var values = p.Values.ToDictionary(v => v.Key, v => v.Value);

        sb.AppendLine("<b>Would be entered</b>");
        foreach (var key in AutofillLabels.Keys.Where(values.ContainsKey))
            sb.AppendLine($"{AutofillLabels.Of(key)} → {ValueText(key, values[key])}");
        sb.AppendLine($"CV → {(p.HasCv ? "attached" : "<i>not set</i>")}");

        if (p.Missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"<b>Not set</b>: {string.Join(", ", p.Missing.Select(AutofillLabels.Of))}");
        }

        sb.AppendLine();
        sb.Append("<i>Filling the form in a browser is not switched on yet, so this is only a preview. Nothing was filled or sent.</i>");
        return new Screen(sb.ToString(), settings);
    }

    private static string ValueText(AutofillKey key, string value) =>
        // A cover letter is long: say that it is there, do not print it.
        key == AutofillKey.CoverLetter ? $"template, {value.Length} characters" : Html(Short(value, ValuePreviewLength));
}

/// <summary>
/// ✅ Applied and ⏭ Skip: the same steps, a different change. The core's answer is idempotent (tapping again succeeds), and a
/// refusal (the vacancy is gone, or was already marked the other way) is only a toast: the message is not touched.
/// On success the buttons under the vacancy become one button that shows the new state.
/// </summary>
internal abstract class VacancyTrackHandler : ICallbackHandler
{
    protected abstract Task<Result<ApplicationDto, Error>> TrackAsync(CallbackContext ctx);
    protected abstract string ToastText { get; }
    protected abstract InlineKeyboardMarkup StateKeyboard(int deliveryId);

    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var result = await TrackAsync(ctx);
        if (result.IsFailure) return CallbackResult.Answer(result.Error.Message);

        await ctx.Io.EditKeyboardAsync(ctx.ChatId, ctx.MessageId, StateKeyboard(ctx.Data.DeliveryId), ctx.Ct);
        return CallbackResult.Answer(ToastText);
    }
}

[Handles(CallbackAction.VacancyApplied)]
internal sealed class VacancyAppliedHandler(IWorkGregClient client) : VacancyTrackHandler
{
    protected override Task<Result<ApplicationDto, Error>> TrackAsync(CallbackContext ctx) =>
        client.MarkAppliedAsync(ctx.ChatId, ctx.Data.DeliveryId, ctx.Ct);

    protected override string ToastText => "Marked as applied";

    protected override InlineKeyboardMarkup StateKeyboard(int deliveryId) =>
        Kb(Row(Btn("✅ Applied", CallbackData.VacancyApplied(deliveryId))));
}

[Handles(CallbackAction.VacancySkip)]
internal sealed class VacancySkipHandler(IWorkGregClient client) : VacancyTrackHandler
{
    protected override Task<Result<ApplicationDto, Error>> TrackAsync(CallbackContext ctx) =>
        client.SkipAsync(ctx.ChatId, ctx.Data.DeliveryId, ctx.Ct);

    protected override string ToastText => "Skipped";

    protected override InlineKeyboardMarkup StateKeyboard(int deliveryId) =>
        Kb(Row(Btn("⏭ Skipped", CallbackData.VacancySkip(deliveryId))));
}
