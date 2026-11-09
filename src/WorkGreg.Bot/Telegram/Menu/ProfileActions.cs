using WorkGreg.Client;
using WorkGreg.Services;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

// Buttons that change a profile. Each one: do the change through the client, then show the screen the user was on.

[Handles(CallbackAction.ToggleEnabled)]
internal sealed class ToggleEnabledHandler(IWorkGregClient client, ProfileScreen profileScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var p = await client.TryLoadAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        if (p is null) return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));

        var enable = !p.Enabled;
        var result = await client.SetEnabledAsync(ctx.ChatId, p.Id, enable, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Fail(result.Error), ctx.Ct));

        var screen = await profileScreen.RenderAsync(ctx.ChatId, p.Id, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = enable ? "Profile enabled" : "Profile disabled" });
    }
}

[Handles(CallbackAction.ToggleRemote)]
internal sealed class ToggleRemoteHandler(IWorkGregClient client, FiltersScreen filtersScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var p = await client.TryLoadAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        if (p is null) return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));

        var on = !p.RemoteOnly;
        var result = await client.SetRemoteOnlyAsync(ctx.ChatId, p.Id, on, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await filtersScreen.RenderAsync(ctx.ChatId, p.Id, Fail(result.Error), ctx.Ct));

        var screen = await filtersScreen.RenderAsync(ctx.ChatId, p.Id, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = $"Remote only: {(on ? "on" : "off")}" });
    }
}

[Handles(CallbackAction.RemoveConfirm)]
internal sealed class RemoveProfileHandler(IWorkGregClient client, ProfilesScreen profilesScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var result = await client.RemoveProfileAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Notice(result, ProfileRemoved), ctx.Ct));
    }
}

/// <summary>Switches a source on or off for a profile, and stays on the same region page.</summary>
[Handles(CallbackAction.ToggleSource)]
internal sealed class ToggleSourceHandler(IWorkGregClient client, SourcesScreen sourcesScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var data = ctx.Data;
        var result = await client.ToggleSourceAsync(ctx.ChatId, data.ProfileId, data.Provider!, ctx.Ct);

        if (result.IsFailure)
        {
            // The profile is gone: go to the list. Anything else (unknown source, limit reached): stay and say why.
            var failed = result.Error.Kind == ErrorKind.NotFound
                ? await profilesScreen.RenderAsync(ctx.ChatId, Fail(result.Error), ctx.Ct)
                : await sourcesScreen.RenderAsync(ctx.ChatId, data.ProfileId, data.Region, Fail(result.Error), ctx.Ct);
            return CallbackResult.Show(failed);
        }

        var screen = await sourcesScreen.RenderAsync(ctx.ChatId, data.ProfileId, data.Region, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = result.Value.Added ? "Source added" : "Source removed" });
    }
}