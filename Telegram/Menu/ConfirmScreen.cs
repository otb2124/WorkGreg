using WorkGreg.Services;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>"Resend all matches?" The tap does not change anything; ✅ leads to <see cref="ResendHandler"/>.</summary>
[Handles(CallbackAction.ResendAsk)]
internal sealed class ResendConfirmScreen(JobService jobs) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var total = await jobs.CountStoredAsync(ctx.ChatId, ctx.Ct);
        var kb = Kb(Row(Btn("🔁 Yes, reset", CallbackData.ResendConfirm), Btn("✖ Cancel", CallbackData.Home)));

        return CallbackResult.Show(new Screen(
            "<b>🔁 Resend all matches?</b>\n" +
            $"{total} stored vacancies will be marked as not yet sent. " +
            "The next check sends every one that matches your profiles again " +
            "(up to 20 per run, the rest follow on later runs).",
            kb));
    }
}

/// <summary>"Remove profile X?" The tap does not change anything; 🗑 leads to <see cref="RemoveProfileHandler"/>.</summary>
[Handles(CallbackAction.RemoveAsk)]
internal sealed class RemoveConfirmScreen(ProfileService profiles, ProfilesScreen profilesScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var d = await profiles.TryLoadAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        if (d is null)
            return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));

        var p = d.Profile;
        var kb = Kb(Row(Btn("🗑 Yes, remove", CallbackData.RemoveConfirm(p.Id)), Btn("✖ Cancel", CallbackData.Profile(p.Id))));

        return CallbackResult.Show(new Screen(
            $"<b>Remove profile {Html(p.Name)}?</b>\n" +
            "Its sources and stored vacancies are forgotten too, so creating it again sends them again. " +
            "To just search for something else, use ✏️ Keyword instead.",
            kb));
    }
}