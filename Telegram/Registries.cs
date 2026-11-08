using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WorkGreg.Telegram;

/// <summary>Slash command name to command class. Built once at startup.</summary>
internal sealed class CommandRegistry
{
    private readonly Dictionary<string, Type> _types;

    public CommandRegistry(IReadOnlyList<(Type Type, CommandAttribute Attr)> commands)
    {
        _types = RouteScanner.BuildMap(
            commands.Select(c => (c.Type, (BotRouteAttribute)c.Attr)),
            StringComparer.Ordinal,
            name => name.TrimStart('/').ToLowerInvariant());

        MenuEntries = commands
            .Where(c => c.Attr.MenuText is not null)
            .OrderBy(c => c.Attr.MenuOrder)
            .Select(c => (Name: c.Attr.Names[0].TrimStart('/').ToLowerInvariant(), Text: c.Attr.MenuText!))
            .ToList();
    }

    /// <summary>The commands shown in Telegram's "Menu" button, in order.</summary>
    public IReadOnlyList<(string Name, string Text)> MenuEntries { get; }

    /// <summary><paramref name="name"/> is the lower-case name without "/" (see <see cref="CommandLine"/>).</summary>
    public bool TryGet(string name, out Type type) => _types.TryGetValue(name, out type!);
}

/// <summary>Callback data to handler class, for the buttons that have their own class.</summary>
internal sealed class CallbackRegistry
{
    private readonly Dictionary<string, Type> _types;

    public CallbackRegistry(IReadOnlyList<(Type Type, CallbackAttribute Attr)> callbacks) =>
        _types = RouteScanner.BuildMap(
            callbacks.Select(c => (c.Type, (BotRouteAttribute)c.Attr)),
            StringComparer.Ordinal,
            name => name);

    public bool TryGet(string data, out Type type) => _types.TryGetValue(data, out type!);
}

/// <summary>Finds handler classes by attribute and fails at startup (not at the first message) on a wiring mistake.</summary>
internal static class RouteScanner
{
    public static List<(Type Type, TAttr Attr)> Find<THandler, TAttr>(Assembly assembly) where TAttr : BotRouteAttribute
    {
        var found = new List<(Type, TAttr)>();
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(THandler).IsAssignableFrom(type)) continue;

            var attr = type.GetCustomAttribute<TAttr>()
                ?? throw new InvalidOperationException(
                    $"{type.Name} implements {typeof(THandler).Name} but has no [{typeof(TAttr).Name.Replace("Attribute", "")}] attribute, so nothing can route to it.");
            found.Add((type, attr));
        }
        return found;
    }

    public static Dictionary<string, Type> BuildMap(
        IEnumerable<(Type Type, BotRouteAttribute Attr)> routes, StringComparer comparer, Func<string, string> normalize)
    {
        var map = new Dictionary<string, Type>(comparer);
        foreach (var (type, attr) in routes)
            foreach (var raw in attr.Names)
            {
                var name = normalize(raw);
                if (!map.TryAdd(name, type))
                    throw new InvalidOperationException($"'{name}' is claimed by both {map[name].Name} and {type.Name}.");
            }
        return map;
    }

    public static IServiceCollection AddBotRoutes(this IServiceCollection services, Assembly assembly)
    {
        var commands = Find<IBotCommand, CommandAttribute>(assembly);
        var callbacks = Find<IBotCallback, CallbackAttribute>(assembly);

        // Scoped: handlers use the scoped services (ProfileService, MenuHandler, ...) and live for one update.
        foreach (var (type, _) in commands) services.TryAddScoped(type);
        foreach (var (type, _) in callbacks) services.TryAddScoped(type);

        services.AddSingleton(new CommandRegistry(commands));
        services.AddSingleton(new CallbackRegistry(callbacks));
        return services;
    }
}