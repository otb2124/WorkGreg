using WorkGreg.Client;
using WorkGreg.Services;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

// Buttons that change a profile. Each one: do the change through the client, then show the screen the user was on.
// The change answers with the whole updated profile, so the screen is drawn from that answer: no read before, none after.

[Handles(CallbackAction.ToggleEnabled)]
internal sealed class ToggleEnabledHandler(IWorkGregClient client, ProfileScreen profileScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var id = ctx.Data.ProfileId;

        bool enable;
        if (ctx.Data.Desired is { } desired)
        {
            enable = desired;
        }
        else
        {
            // A button sent before buttons carried their value. Remove this branch once old menus are out of chats.
            var current = await client.TryLoadAsync(ctx.ChatId, id, ctx.Ct);
            if (current is null) return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));
            enable = !current.Enabled;
        }

        var result = await client.SetEnabledAsync(ctx.ChatId, id, enable, ctx.Ct);
        if (result.IsFailure)
        {
            var notice = result.Error.Kind == ErrorKind.NotFound ? Warn(NoSuchProfile) : Fail(result.Error);
            return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, notice, ctx.Ct));
        }

        var screen = profileScreen.Render(result.Value, null);
        return CallbackResult.Show(screen with { Toast = enable ? "Profile enabled" : "Profile disabled" });
    }
}

[Handles(CallbackAction.ToggleRemote)]
internal sealed class ToggleRemoteHandler(IWorkGregClient client, FiltersScreen filtersScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var id = ctx.Data.ProfileId;

        bool on;
        if (ctx.Data.Desired is { } desired)
        {
            on = desired;
        }
        else
        {
            // A button sent before buttons carried their value. Remove this branch once old menus are out of chats.
            var current = await client.TryLoadAsync(ctx.ChatId, id, ctx.Ct);
            if (current is null) return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));
            on = !current.RemoteOnly;
        }

        var result = await client.SetRemoteOnlyAsync(ctx.ChatId, id, on, ctx.Ct);
        if (result.IsFailure)
        {
            return result.Error.Kind == ErrorKind.NotFound
                ? CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct))
                : CallbackResult.Show(await filtersScreen.RenderAsync(ctx.ChatId, id, Fail(result.Error), ctx.Ct));
        }

        var screen = filtersScreen.Render(result.Value, null);
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