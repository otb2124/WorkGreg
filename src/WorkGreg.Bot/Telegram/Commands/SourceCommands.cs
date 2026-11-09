using WorkGreg.Client;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

[Command("addsource")]
internal sealed class AddSourceCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        if (!ArgParser.TryId(ctx.Args, out var id, out var rest) || rest.Length == 0)
        {
            var meta = await client.GetMetaAsync(ctx.Ct);
            return Usage($"/addsource &lt;profile id&gt; &lt;source&gt;\nSources: {meta.ProviderIds}\nExample: /addsource 1 remotive");
        }

        return Render(await client.AddSourceAsync(ctx.ChatId, id, ArgParser.FirstWord(rest), ctx.Ct), SourceAdded);
    }
}

[Command("removesource")]
internal sealed class RemoveSourceCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        if (!ArgParser.TryId(ctx.Args, out var id, out var rest) || rest.Length == 0)
            return Usage("/removesource &lt;profile id&gt; &lt;source&gt;");

        return Render(await client.RemoveSourceAsync(ctx.ChatId, id, ArgParser.FirstWord(rest), ctx.Ct), SourceRemoved);
    }
}