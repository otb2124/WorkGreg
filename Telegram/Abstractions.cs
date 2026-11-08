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
/// A button tap whose callback data is handled by its own class, found by <see cref="CallbackAttribute"/>.
/// Data that no class claims goes to <see cref="MenuHandler"/>.
/// </summary>
internal interface IBotCallback
{
    Task ExecuteAsync(CallbackContext ctx);
}

/// <param name="Name">The name the user typed, lower case, without "/" and "@botname" (so aliases can be told apart).</param>
/// <param name="Args">Everything after the name, trimmed. Empty when there is none.</param>
internal sealed record CommandContext(long ChatId, string Name, string Args, IBotIo Io, CancellationToken Ct);

internal sealed record CallbackContext(string QueryId, long ChatId, int MessageId, string Data, IBotIo Io, CancellationToken Ct);

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

/// <summary>Whole callback data strings, e.g. <c>[Callback("stop")]</c>. Case-sensitive.</summary>
internal sealed class CallbackAttribute(params string[] names) : BotRouteAttribute(names);