using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

[Command("addsource")]
internal sealed class AddSourceCommand(SourceService sources) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        if (!ArgParser.TryId(ctx.Args, out var id, out var rest) || rest.Length == 0)
            return Usage($"/addsource &lt;profile id&gt; &lt;source&gt;\nSources: {CommandText.SourceIds()}\nExample: /addsource 1 remotive");

        return Render(await sources.AddAsync(ctx.ChatId, id, ArgParser.FirstWord(rest), ctx.Ct), SourceAdded);
    }
}

[Command("removesource")]
internal sealed class RemoveSourceCommand(SourceService sources) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        if (!ArgParser.TryId(ctx.Args, out var id, out var rest) || rest.Length == 0)
            return Usage("/removesource &lt;profile id&gt; &lt;source&gt;");

        return Render(await sources.RemoveByProviderAsync(ctx.ChatId, id, ArgParser.FirstWord(rest), ctx.Ct), SourceRemoved);
    }
}