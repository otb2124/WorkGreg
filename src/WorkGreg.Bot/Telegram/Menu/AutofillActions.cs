using System.Text;
using WorkGreg.Client;
using WorkGreg.Domain;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

// Buttons that change a profile's autofill settings. Same shape as ProfileActions: do the change through the client,
// then draw the screen the user was on from the settings the core answered with (no second read of them).
// A failure shows the same screen with the reason; when the profile itself is gone the screens fall back to the profile list.

/// <summary>The 🗑 next to a standard value.</summary>
[Handles(CallbackAction.AutofillClearValue)]
internal sealed class AutofillClearValueHandler(IWorkGregClient client, AutofillScreen screen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var profileId = ctx.Data.ProfileId;
        var key = ctx.Data.Key!.Value;

        var result = await client.ClearAutofillValueAsync(ctx.ChatId, profileId, key, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await screen.RenderAsync(ctx.ChatId, profileId, Fail(result.Error), ctx.Ct));

        var shown = await screen.RenderAsync(ctx.ChatId, profileId, null, ctx.Ct, result.Value);
        return CallbackResult.Show(shown with { Toast = $"{AutofillLabels.Of(key)} cleared" });
    }
}

/// <summary>The 🗑 next to the CV.</summary>
[Handles(CallbackAction.AutofillDeleteDoc)]
internal sealed class AutofillDeleteDocumentHandler(IWorkGregClient client, AutofillScreen screen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var profileId = ctx.Data.ProfileId;

        var result = await client.DeleteAutofillDocumentAsync(ctx.ChatId, profileId, AutofillDocumentKind.Cv, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await screen.RenderAsync(ctx.ChatId, profileId, Fail(result.Error), ctx.Ct));

        var shown = await screen.RenderAsync(ctx.ChatId, profileId, null, ctx.Ct, result.Value);
        return CallbackResult.Show(shown with { Toast = "CV removed" });
    }
}

/// <summary>The 🗑 Delete of one custom answer. Back on the list of answers afterwards.</summary>
[Handles(CallbackAction.AutofillDeleteRule)]
internal sealed class AutofillDeleteRuleHandler(IWorkGregClient client, AutofillRulesScreen screen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var profileId = ctx.Data.ProfileId;

        var result = await client.DeleteAutofillRuleAsync(ctx.ChatId, profileId, ctx.Data.ItemId, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await screen.RenderAsync(ctx.ChatId, profileId, Fail(result.Error), ctx.Ct));

        var shown = await screen.RenderAsync(ctx.ChatId, profileId, null, ctx.Ct, result.Value);
        return CallbackResult.Show(shown with { Toast = "Answer deleted" });
    }
}

/// <summary>
/// "📋 Copy from profile…". Without a source it lists the user's other profiles to pick from; with one (the tap on a
/// profile in that list) it copies: the target's values, custom answers and CV are replaced by the source's.
/// </summary>
[Handles(CallbackAction.AutofillCopy)]
internal sealed class AutofillCopyHandler(
    IWorkGregClient client, AutofillScreen autofillScreen, ProfilesScreen profilesScreen, PendingInputStore pending)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        pending.Clear(ctx.ChatId);

        return ctx.Data.FromProfileId == 0
            ? CallbackResult.Show(await PickAsync(ctx))
            : CallbackResult.Show(await CopyAsync(ctx));
    }

    private async Task<Screen> PickAsync(CallbackContext ctx)
    {
        var targetId = ctx.Data.ProfileId;

        var target = await client.TryLoadAsync(ctx.ChatId, targetId, ctx.Ct);
        if (target is null) return await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct);

        var others = (await client.ListProfilesAsync(ctx.ChatId, ctx.Ct)).Where(p => p.Id != targetId).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"<b>📋 Copy AutoFill settings to {Html(target.Name)}</b>");
        sb.AppendLine();
        sb.Append(others.Count == 0
            ? "<i>You have no other profiles to copy from.</i>"
            : "Pick the profile to copy from. Its values, custom answers and CV <b>replace</b> what this profile has now.");

        var rows = others
            .Select(p => Row(Btn(Short(p.Name, 40), CallbackData.AutofillCopy(targetId, p.Id))))
            .ToList();
        rows.Add(Row(Btn("⬅ Back", CallbackData.Autofill(targetId))));

        return new Screen(sb.ToString(), Kb(rows.ToArray()));
    }

    private async Task<Screen> CopyAsync(CallbackContext ctx)
    {
        var targetId = ctx.Data.ProfileId;

        var source = await client.TryLoadAsync(ctx.ChatId, ctx.Data.FromProfileId, ctx.Ct);
        if (source is null)
            return await autofillScreen.RenderAsync(ctx.ChatId, targetId, Warn(NoSuchProfile), ctx.Ct);

        var result = await client.CopyAutofillAsync(ctx.ChatId, source.Id, targetId, ctx.Ct);
        if (result.IsFailure)
            return await autofillScreen.RenderAsync(ctx.ChatId, targetId, Fail(result.Error), ctx.Ct);

        var shown = await autofillScreen.RenderAsync(
            ctx.ChatId, targetId, Ok($"Copied AutoFill settings from <b>{Html(source.Name)}</b>."), ctx.Ct, result.Value);
        return shown with { Toast = "Copied" };
    }
}
