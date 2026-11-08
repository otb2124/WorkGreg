using WorkGreg.Services;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

// Buttons that change a profile. Each one: do the change through the service, then show the screen the user was on.

[Handles(CallbackAction.ToggleEnabled)]
internal sealed class ToggleEnabledHandler(ProfileService profiles, ProfileScreen profileScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var d = await profiles.TryLoadAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        if (d is null) return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));

        var enable = !d.Profile.Enabled;
        var result = await profiles.SetEnabledAsync(ctx.ChatId, d.Profile.Id, enable, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Fail(result.Error), ctx.Ct));

        var screen = await profileScreen.RenderAsync(ctx.ChatId, d.Profile.Id, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = enable ? "Profile enabled" : "Profile disabled" });
    }
}

[Handles(CallbackAction.ToggleRemote)]
internal sealed class ToggleRemoteHandler(ProfileService profiles, FiltersScreen filtersScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var d = await profiles.TryLoadAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        if (d is null) return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Warn(NoSuchProfile), ctx.Ct));

        var on = !d.Profile.RemoteOnly;
        var result = await profiles.SetRemoteOnlyAsync(ctx.ChatId, d.Profile.Id, on, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await filtersScreen.RenderAsync(ctx.ChatId, d.Profile.Id, Fail(result.Error), ctx.Ct));

        var screen = await filtersScreen.RenderAsync(ctx.ChatId, d.Profile.Id, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = $"Remote only: {(on ? "on" : "off")}" });
    }
}

[Handles(CallbackAction.RemoveConfirm)]
internal sealed class RemoveProfileHandler(ProfileService profiles, ProfilesScreen profilesScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var result = await profiles.RemoveAsync(ctx.ChatId, ctx.Data.ProfileId, ctx.Ct);
        return CallbackResult.Show(await profilesScreen.RenderAsync(ctx.ChatId, Notice(result, ProfileRemoved), ctx.Ct));
    }
}

/// <summary>Switches a source on or off for a profile, and stays on the same region page.</summary>
[Handles(CallbackAction.ToggleSource)]
internal sealed class ToggleSourceHandler(SourceService sources, SourcesScreen sourcesScreen, ProfilesScreen profilesScreen)
    : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var data = ctx.Data;
        var result = await sources.ToggleAsync(ctx.ChatId, data.ProfileId, data.Provider!, ctx.Ct);

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