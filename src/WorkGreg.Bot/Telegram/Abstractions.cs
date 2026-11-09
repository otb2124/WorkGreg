namespace WorkGreg.Telegram;

/// <summary>
/// A slash command (<c>/include 2 helpdesk</c>). One class per command, found by <see cref="CommandAttribute"/>.
/// Instances are resolved from a per-update DI scope, so a command takes the scoped services it needs in its constructor.
/// </summary>
internal interface IBotCommand
{
    Task ExecuteAsync(CommandContext ctx);
}

/// <summary>
/// A button tap. One class per <see cref="CallbackAction"/>, found by <see cref="HandlesAttribute"/> and run by the
/// <see cref="CallbackRouter"/>. Like commands, handlers are resolved from the per-update DI scope.
/// </summary>
internal interface ICallbackHandler
{
    Task<CallbackResult> HandleAsync(CallbackContext ctx);
}

/// <param name="Name">The name the user typed, lower case, without "/" and "@botname" (so aliases can be told apart).</param>
/// <param name="Args">Everything after the name, trimmed. Empty when there is none.</param>
internal sealed record CommandContext(long ChatId, string Name, string Args, IBotIo Io, CancellationToken Ct);

internal sealed record CallbackContext(string QueryId, long ChatId, int MessageId, CallbackData Data, IBotIo Io, CancellationToken Ct);

/// <summary>What the router should do after a handler ran.</summary>
internal abstract record CallbackResult
{
    /// <summary>Replace the tapped message with a screen (its <see cref="Screen.Toast"/>, if any, is shown on the tap).</summary>
    public sealed record Edit(Screen Screen) : CallbackResult;

    /// <summary>Only answer the tap, with a toast or silently. The message stays as it is.</summary>
    public sealed record Toast(string? Text) : CallbackResult;

    /// <summary>The handler already answered the tap and updated the message itself.</summary>
    public sealed record Done : CallbackResult;

    public static readonly CallbackResult Handled = new Done();

    public static CallbackResult Show(Screen screen) => new Edit(screen);
    public static CallbackResult Answer(string? toast) => new Toast(toast);
}

/// <summary>Names a handler answers to. Checked once at startup (<see cref="RouteScanner"/>).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal abstract class BotRouteAttribute(params string[] names) : Attribute
{
    public IReadOnlyList<string> Names { get; } = names;
}

/// <summary>Slash command names, with or without the leading "/": <c>[Command("profiles", "sources")]</c>.</summary>
internal sealed class CommandAttribute(params string[] names) : BotRouteAttribute(names)
{
    /// <summary>When set, the command is listed in Telegram's "Menu" button (under its first name).</summary>
    public string? MenuText { get; init; }

    /// <summary>Position in that list. Commands without a position come last.</summary>
    public int MenuOrder { get; init; } = int.MaxValue;
}

/// <summary>The button actions a callback handler class serves: <c>[Handles(CallbackAction.Home)]</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal sealed class HandlesAttribute(params CallbackAction[] actions) : Attribute
{
    public IReadOnlyList<CallbackAction> Actions { get; } = actions;
}