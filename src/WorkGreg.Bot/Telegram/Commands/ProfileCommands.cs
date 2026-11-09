using System.Text;
using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

[Command("profiles", "sources")]
internal sealed class ListProfilesCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        var all = await client.ListProfilesAsync(ctx.ChatId, ctx.Ct);
        if (all.Count == 0) return "No profiles yet. Create one with /addprofile &lt;keyword&gt;";

        var sb = new StringBuilder();
        foreach (var p in all)
        {
            sb.AppendLine($"#{p.Id} <b>{Html(p.Name)}</b> {(p.Enabled ? "▶️" : "⏸ disabled")}");
            sb.AppendLine($"Keyword: {Html(p.Keyword)}");
            sb.AppendLine(p.Sources.Count == 0
                ? "Sources: <i>none yet</i>, add one with /addsource"
                : "Sources: " + string.Join(", ", p.Sources.Select(SourceLabel)));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

[Command("profile", "filters")]
internal sealed class ShowProfileCommand(IWorkGregClient client) : ProfileCommand
{
    protected override string UsageText => "/profile &lt;id&gt;";

    protected override async Task<string> RunAsync(CommandContext ctx, int profileId, string rest) =>
        Render(await client.GetProfileAsync(ctx.ChatId, profileId, ctx.Ct), ProfileCard);

    private static string ProfileCard(ProfileDto p)
    {
        static string Show(IReadOnlyList<string> l, string empty) => l.Count > 0 ? Html(ListText(l)) : $"<i>{empty}</i>";

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(p.Name)}</b> #{p.Id} {(p.Enabled ? "▶️ active" : "⏸ disabled")}");
        sb.AppendLine($"Keyword:    {Html(p.Keyword)}");
        sb.AppendLine($"Sources:    {(p.Sources.Count == 0 ? "<i>none</i>" : string.Join(", ", p.Sources.Select(SourceLabel)))}");
        sb.AppendLine($"Include:    {Show(p.Include, "anything")}");
        sb.AppendLine($"Exclude:    {Show(p.Exclude, "nothing")}");
        sb.AppendLine($"Companies:  {Show(p.Companies, "any")}");
        sb.AppendLine($"Locations:  {Show(p.Locations, "any")}");
        sb.AppendLine($"Salary:     {(p.MinSalary is { } minSalary ? $">= {minSalary}" : "<i>any</i>")}");
        sb.Append($"Remote only: {(p.RemoteOnly ? "yes" : "no")}");
        return sb.ToString();
    }
}

[Command("addprofile")]
internal sealed class AddProfileCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            var meta = await client.GetMetaAsync(ctx.Ct);
            return Usage($"/addprofile &lt;keyword&gt;  (1-{meta.MaxKeywordLength} characters)\nExample: /addprofile full stack dev");
        }

        return Render(await client.CreateProfileAsync(ctx.ChatId, ctx.Args, ctx.Ct), ProfileAdded);
    }
}

[Command("removeprofile")]
internal sealed class RemoveProfileCommand(IWorkGregClient client) : ProfileEditCommand
{
    protected override string UsageText => "/removeprofile &lt;id&gt;";
    protected override Task<Result<ProfileDto, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        client.RemoveProfileAsync(ctx.ChatId, profileId, ctx.Ct);
    protected override string OkText(ProfileDto p) => ProfileRemoved(p);
}

[Command("name")]
internal sealed class RenameProfileCommand(IWorkGregClient client) : ProfileEditCommand
{
    protected override string UsageText => "/name &lt;id&gt; &lt;name&gt;";
    protected override Task<Result<ProfileDto, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        client.RenameProfileAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(ProfileDto p) => ProfileRenamed(p);
}

[Command("keyword")]
internal sealed class SetKeywordCommand(IWorkGregClient client) : ProfileEditCommand
{
    protected override string UsageText => "/keyword &lt;id&gt; &lt;new keyword&gt;";
    protected override Task<Result<ProfileDto, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        client.SetKeywordAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(ProfileDto p) => KeywordChanged(p);
}

[Command("enable")]
internal sealed class EnableProfileCommand(IWorkGregClient client) : ProfileEditCommand
{
    protected override string UsageText => "/enable &lt;id&gt;";
    protected override Task<Result<ProfileDto, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        client.SetEnabledAsync(ctx.ChatId, profileId, true, ctx.Ct);
    protected override string OkText(ProfileDto p) => EnabledChanged(p);
}

[Command("disable")]
internal sealed class DisableProfileCommand(IWorkGregClient client) : ProfileEditCommand
{
    protected override string UsageText => "/disable &lt;id&gt;";
    protected override Task<Result<ProfileDto, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        client.SetEnabledAsync(ctx.ChatId, profileId, false, ctx.Ct);
    protected override string OkText(ProfileDto p) => EnabledChanged(p);
}