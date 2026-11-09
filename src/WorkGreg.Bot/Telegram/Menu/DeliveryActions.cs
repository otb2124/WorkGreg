using WorkGreg.Client;
using WorkGreg.Domain;
using static WorkGreg.Telegram.MenuKit;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

[Handles(CallbackAction.DeliveryInstant)]
internal sealed class DeliveryInstantHandler(IWorkGregClient client, DeliveryScreen deliveryScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var result = await client.SetDeliveryAsync(ctx.ChatId, DeliveryMode.Instant, null, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await deliveryScreen.RenderAsync(ctx.ChatId, Fail(result.Error), ctx.Ct));

        var screen = await deliveryScreen.RenderAsync(ctx.ChatId, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = "Delivery: instant" });
    }
}

/// <summary>A button of the hour picker. The service checks the range, so a hand-made value is refused there.</summary>
[Handles(CallbackAction.DeliveryHour)]
internal sealed class DeliveryHourHandler(IWorkGregClient client, DeliveryScreen deliveryScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var hour = ctx.Data.Hour;

        var result = await client.SetDeliveryAsync(ctx.ChatId, DeliveryMode.Digest, hour, ctx.Ct);
        if (result.IsFailure)
            return CallbackResult.Show(await deliveryScreen.RenderAsync(ctx.ChatId, Fail(result.Error), ctx.Ct));

        var screen = await deliveryScreen.RenderAsync(ctx.ChatId, null, ctx.Ct);
        return CallbackResult.Show(screen with { Toast = $"Digest at {hour:00}:00 UTC" });
    }
}

/// <summary>Pause or resume alerts (all profiles).</summary>
[Handles(CallbackAction.Pause)]
internal sealed class PauseHandler(IWorkGregClient client, MainScreen mainScreen) : ICallbackHandler
{
    public async Task<CallbackResult> HandleAsync(CallbackContext ctx)
    {
        var result = await client.SetPausedAsync(ctx.ChatId, ctx.Data.Flag, ctx.Ct);
        return CallbackResult.Show(await mainScreen.RenderAsync(ctx.ChatId, Notice(result, Paused), ctx.Ct));
    }
}