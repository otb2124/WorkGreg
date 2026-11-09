using System.Reflection;

namespace WorkGreg.Telegram;

/// <summary>
/// Callback action to handler class. Built once at startup and checked there: an action nobody handles, or two
/// classes claiming the same action, stops the app before the first tap instead of failing in front of a user.
/// </summary>
internal sealed class CallbackRouter
{
    private readonly Dictionary<CallbackAction, Type> _handlers = new();

    public CallbackRouter(IEnumerable<(Type Type, HandlesAttribute Attr)> handlers)
    {
        CallbackData.VerifyWireCodes();

        foreach (var (type, attr) in handlers)
            foreach (var action in attr.Actions)
                if (!_handlers.TryAdd(action, type))
                    throw new InvalidOperationException($"{action} is claimed by both {_handlers[action].Name} and {type.Name}.");

        var missing = Enum.GetValues<CallbackAction>().Where(a => !_handlers.ContainsKey(a)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"No class handles {string.Join(", ", missing)}. Add [Handles(CallbackAction.X)] to a class implementing {nameof(ICallbackHandler)}.");
    }

    /// <summary>Finds every <see cref="ICallbackHandler"/> in the assembly. A handler without [Handles] is a wiring mistake.</summary>
    public static List<(Type Type, HandlesAttribute Attr)> Find(Assembly assembly)
    {
        var found = new List<(Type, HandlesAttribute)>();
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(ICallbackHandler).IsAssignableFrom(type)) continue;

            var attr = type.GetCustomAttribute<HandlesAttribute>()
                ?? throw new InvalidOperationException(
                    $"{type.Name} implements {nameof(ICallbackHandler)} but has no [Handles] attribute, so no button can reach it.");
            found.Add((type, attr));
        }
        return found;
    }

    /// <summary>Runs the handler of the tapped button, without answering the tap. Used when one handler delegates to another.</summary>
    public Task<CallbackResult> ResolveAsync(IServiceProvider services, CallbackContext ctx)
    {
        var handler = (ICallbackHandler)services.GetRequiredService(_handlers[ctx.Data.Action]);
        return handler.HandleAsync(ctx);
    }

    /// <summary>Runs the handler and does what its result says: edit the menu message, show a toast, or nothing.</summary>
    public async Task RouteAsync(IServiceProvider services, CallbackContext ctx)
    {
        switch (await ResolveAsync(services, ctx))
        {
            case CallbackResult.Edit edit:
                await ctx.Io.AnswerCallbackAsync(ctx.QueryId, edit.Screen.Toast, ctx.Ct);
                await ctx.Io.EditScreenAsync(ctx.ChatId, ctx.MessageId, edit.Screen, ctx.Ct);
                break;

            case CallbackResult.Toast toast:
                await ctx.Io.AnswerCallbackAsync(ctx.QueryId, toast.Text, ctx.Ct);
                break;

                // Done: the handler talked to Telegram itself.
        }
    }
}