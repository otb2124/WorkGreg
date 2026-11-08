using WorkGreg.Services;

namespace WorkGreg.Telegram.Menu;

/// <summary>
/// "➕ New profile" and every "✏️ / 🔤 / 🚫 ..." button: remember what this chat is being asked for,
/// and show the prompt. The next plain text message is the answer (<see cref="PendingInputHandler"/>).
/// </summary>
[Handles(CallbackAction.NewProfile, CallbackAction.Ask)]
internal sealed class AskHandler(PendingInputStore pending, PromptScreen promptScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var target = ctx.Data.Action == CallbackAction.NewProfile ? PromptTarget.NewProfile() : ctx.Data.Prompt!.Value;

        pending.Set(ctx.ChatId, new PendingInput(target, ctx.MessageId));
        return CallbackResult.Show(await promptScreen.RenderAsync(ctx.ChatId, target, null, ctx.Ct));
    }
}

/// <summary>The 🗑 Clear button of a filter prompt.</summary>
[Handles(CallbackAction.Clear)]
internal sealed class ClearHandler(ProfileService profiles, PendingInputStore pending, FiltersScreen filtersScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        pending.Clear(ctx.ChatId);

        var (kind, id) = ctx.Data.Prompt!.Value;

        // Clearing can only fail when the profile is gone; the filters page then shows "No such profile."
        switch (kind)
        {
            case PromptKind.Include: await profiles.SetIncludeAsync(ctx.ChatId, id, null, ctx.Ct); break;
            case PromptKind.Exclude: await profiles.SetExcludeAsync(ctx.ChatId, id, null, ctx.Ct); break;
            case PromptKind.Companies: await profiles.SetCompaniesAsync(ctx.ChatId, id, null, ctx.Ct); break;
            case PromptKind.Locations: await profiles.SetLocationsAsync(ctx.ChatId, id, null, ctx.Ct); break;
            case PromptKind.MinSalary: await profiles.SetMinSalaryAsync(ctx.ChatId, id, null, ctx.Ct); break;
        }

        var screen = await filtersScreen.RenderAsync(ctx.ChatId, id, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = "Cleared" });
    }
}

/// <summary>
/// The ✖ Cancel button: does exactly what the button of the screen it goes back to would do. That screen is a
/// <see cref="NavigationHandler"/>, so it also drops the pending prompt.
/// </summary>
[Handles(CallbackAction.Cancel)]
internal sealed class CancelHandler(CallbackRouter router, IServiceProvider services) : ICallbackHandler
{
    public Task<CallbackResult> HandleAsync(CallbackContext ctx) =>
        router.ResolveAsync(services, ctx with { Data = ctx.Data.Back! });
}