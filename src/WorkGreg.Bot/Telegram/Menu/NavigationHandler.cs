namespace WorkGreg.Telegram.Menu;

/// <summary>
/// Base of the screens a button can open (Home, Profiles, Profile, ...). Opening a screen abandons any pending prompt;
/// then the screen renders itself. Screens that are only ever rendered by other handlers do not extend this.
/// </summary>
internal abstract class NavigationHandler(PendingInputStore pending) : ICallbackHandler
{
    protected abstract Task<Screen> OpenAsync(CallbackContext ctx);

    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        pending.Clear(ctx.ChatId);
        return CallbackResult.Show(await OpenAsync(ctx));
    }
}