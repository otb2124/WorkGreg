namespace WorkGreg.Telegram;

/// <summary>A slash command split into its name and the rest of the text.</summary>
internal readonly record struct CommandLine(string Name, string Args)
{
    /// <summary>
    /// "/Include@MyBot 2 a, b" gives Name "include" and Args "2 a, b". False when the text is not a slash command.
    /// </summary>
    public static bool TryParse(string text, out CommandLine line)
    {
        line = default;
        if (!text.StartsWith('/')) return false;

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts[0].ToLowerInvariant();
        var at = name.IndexOf('@');
        if (at > 0) name = name[..at];

        line = new CommandLine(name.TrimStart('/'), parts.Length > 1 ? parts[1] : "");
        return true;
    }
}

/// <summary>Argument parsing shared by the commands.</summary>
internal static class ArgParser
{
    /// <summary>Splits "&lt;#id&gt; rest of the text". False when the first word is not a number.</summary>
    public static bool TryId(string args, out int id, out string rest)
    {
        id = 0;
        var t = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        rest = t.Length > 1 ? t[1] : "";
        return t.Length > 0 && int.TryParse(t[0].TrimStart('#'), out id);
    }

    public static string FirstWord(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}