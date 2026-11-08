using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

/// <summary>A command that answers with one HTML message. Most commands are this.</summary>
internal abstract class TextCommand : IBotCommand
{
    protected abstract Task<string> RunAsync(CommandContext ctx);

    public async Task ExecuteAsync(CommandContext ctx) =>
        await ctx.Io.ReplyAsync(ctx.ChatId, await RunAsync(ctx), ctx.Ct);
}

/// <summary>A text command whose first argument is a profile id: <c>/name 2 Remote jobs</c>.</summary>
internal abstract class ProfileCommand : TextCommand
{
    /// <summary>Shown (HTML-encoded already) when the id is missing or not a number.</summary>
    protected abstract string UsageText { get; }

    protected abstract Task<string> RunAsync(CommandContext ctx, int profileId, string rest);

    protected sealed override async Task<string> RunAsync(CommandContext ctx) =>
        ArgParser.TryId(ctx.Args, out var id, out var rest)
            ? await RunAsync(ctx, id, rest)
            : Usage(UsageText);
}

/// <summary>A profile command that changes the profile and answers with the new state or the service's error.</summary>
internal abstract class ProfileEditCommand : ProfileCommand
{
    protected abstract Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest);

    protected abstract string OkText(SearchProfile profile);

    protected sealed override async Task<string> RunAsync(CommandContext ctx, int profileId, string rest) =>
        Render(await EditAsync(ctx, profileId, rest), OkText);
}

/// <summary>Text used by more than one command.</summary>
internal static class CommandText
{
    public static string SourceIds() => string.Join(", ", SourceCatalog.All.Select(p => p.Id));
}