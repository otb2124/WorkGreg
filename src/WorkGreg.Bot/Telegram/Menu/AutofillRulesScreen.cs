using System.Diagnostics.CodeAnalysis;
using System.Text;
using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Domain;
using global::Telegram.Bot.Types.ReplyMarkups;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>The one-line form of a custom answer, <c>pattern = answer</c>, as it is typed and shown.</summary>
internal static class AutofillRuleText
{
    public const string Help =
        "Send one line: <code>question = answer</code>\n" +
        "When a form field's label, name, id or placeholder contains the question, the answer is entered, " +
        "e.g. <code>work permit = Yes</code>.\n" +
        "Start the question with <code>re:</code> for a regular expression, e.g. <code>re:years? of experience = 5</code>.\n" +
        "Custom answers are checked first, in the order you added them.";

    public const string FormatHint = "Use one line: <code>question = answer</code> (or <code>re:pattern = answer</code>).";

    /// <summary>Splits on the first "=". A pattern starting with <c>re:</c> is a regular expression. Emptiness is the core's to judge.</summary>
    public static bool TryParse(string line, FieldMatchBy matchBy, [NotNullWhen(true)] out AutofillRuleRequest? request)
    {
        request = null;

        var at = line.IndexOf('=');
        if (at < 0) return false;

        var pattern = line[..at].Trim();
        var answer = line[(at + 1)..].Trim();

        var isRegex = pattern.StartsWith("re:", StringComparison.OrdinalIgnoreCase);
        if (isRegex) pattern = pattern[3..].Trim();

        request = new AutofillRuleRequest(pattern, answer, isRegex, matchBy);
        return true;
    }

    /// <summary>The line that <see cref="TryParse"/> would read back.</summary>
    public static string Format(AutofillRuleDto r) => $"{(r.IsRegex ? "re:" : "")}{r.Pattern} = {r.Value}";
}

/// <summary>The custom answers of a profile, in evaluation order, with one numbered button each.</summary>
[Handles(CallbackAction.AutofillRules)]
internal sealed class AutofillRulesScreen(IWorkGregClient client, ProfilesScreen profilesScreen, PendingInputStore pending)
    : NavigationHandler(pending)
{
    // Telegram messages hold 4096 characters; the header and HTML escapes need room. Buttons exist for every answer anyway.
    private const int TextBudget = 3400;
    private const int ButtonsPerRow = 5;

    protected override Task<Screen> OpenAsync(CallbackContext ctx) =>
        RenderAsync(ctx.ChatId, ctx.Data.ProfileId, null, ctx.Ct);

    /// <param name="settings">The settings when the caller already has them (the answer of a change); read from the core when null.</param>
    public async Task<Screen> RenderAsync(
        long chatId, int profileId, string? notice, CancellationToken ct, AutofillSettingsDto? settings = null)
    {
        var p = await client.TryLoadAsync(chatId, profileId, ct);
        if (p is null) return await profilesScreen.RenderAsync(chatId, Warn(NoSuchProfile), ct);

        if (settings is null)
        {
            var loaded = await client.GetAutofillAsync(chatId, profileId, ct);
            if (loaded.IsFailure) return await profilesScreen.RenderAsync(chatId, Fail(loaded.Error), ct);
            settings = loaded.Value;
        }

        return Render(p, settings, notice);
    }

    public Screen Render(ProfileDto p, AutofillSettingsDto a, string? notice)
    {
        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>📋 Custom answers · {Html(p.Name)}</b>");
        sb.AppendLine("<i>Checked in this order, before the standard values.</i>");
        sb.AppendLine();

        if (a.Rules.Count == 0)
        {
            sb.Append("<i>None yet. Tap ➕ Add answer.</i>");
        }
        else
        {
            var shown = 0;
            foreach (var (rule, index) in a.Rules.Select((r, i) => (r, i)))
            {
                var line = $"{index + 1}. {(rule.IsRegex ? "re: " : "")}{Html(Short(rule.Pattern, 30))} → {Html(Short(rule.Value, 30))}";
                if (sb.Length + line.Length > TextBudget) break;

                sb.AppendLine(line);
                shown++;
            }

            if (shown < a.Rules.Count) sb.AppendLine($"…and {a.Rules.Count - shown} more (open them with the numbers).");
            sb.Append("<i>Tap a number to edit or delete that answer.</i>");
        }

        var rows = new List<InlineKeyboardButton[]> { Row(Btn("➕ Add answer", CallbackData.AskAddRule(p.Id))) };

        var numbers = a.Rules
            .Select((r, i) => Btn((i + 1).ToString(), CallbackData.AutofillRule(p.Id, r.Id)))
            .Chunk(ButtonsPerRow);
        rows.AddRange(numbers);

        rows.Add(Row(Btn("⬅ Back", CallbackData.Autofill(p.Id))));

        return new Screen(sb.ToString(), Kb(rows.ToArray()));
    }
}

/// <summary>One custom answer in full, with Edit and Delete.</summary>
[Handles(CallbackAction.AutofillRule)]
internal sealed class AutofillRuleScreen(
    IWorkGregClient client, AutofillRulesScreen rulesScreen, ProfilesScreen profilesScreen, PendingInputStore pending)
    : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) =>
        RenderAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Data.ItemId, null, ctx.Ct);

    public async Task<Screen> RenderAsync(long chatId, int profileId, int ruleId, string? notice, CancellationToken ct)
    {
        var loaded = await client.GetAutofillAsync(chatId, profileId, ct);
        if (loaded.IsFailure) return await profilesScreen.RenderAsync(chatId, Fail(loaded.Error), ct);

        var settings = loaded.Value;
        var position = settings.Rules.ToList().FindIndex(r => r.Id == ruleId);
        if (position < 0)
            return await rulesScreen.RenderAsync(chatId, profileId, Warn("No such custom answer."), ct, settings);

        var rule = settings.Rules[position];
        var matchedAgainst = rule.MatchBy == FieldMatchBy.Any
            ? "label, name, id or placeholder"
            : rule.MatchBy.ToString().ToLowerInvariant();

        var sb = new StringBuilder();
        AppendNotice(sb, notice);
        sb.AppendLine($"<b>📋 Custom answer {position + 1}</b>");
        sb.AppendLine();
        sb.AppendLine($"Question: <code>{Html(Short(rule.Pattern))}</code>{(rule.IsRegex ? " <i>(regular expression)</i>" : "")}");
        sb.AppendLine($"Compared with: {matchedAgainst}");
        sb.Append($"Answer: {Html(Short(rule.Value))}");

        var kb = Kb(
            Row(Btn("✏️ Edit", CallbackData.AskEditRule(profileId, ruleId)),
                Btn("🗑 Delete", CallbackData.AutofillDeleteRule(profileId, ruleId))),
            Row(Btn("⬅ Back", CallbackData.AutofillRules(profileId))));

        return new Screen(sb.ToString(), kb);
    }
}
