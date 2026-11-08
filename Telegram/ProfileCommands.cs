using System.Text;
using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

[Command("profiles", "sources")]
internal sealed class ListProfilesCommand(ProfileService profiles) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        var all = await profiles.ListAsync(ctx.ChatId, ctx.Ct);
        if (all.Count == 0) return "No profiles yet. Create one with /addprofile &lt;keyword&gt;";

        var sb = new StringBuilder();
        foreach (var (p, mine) in all)
        {
            sb.AppendLine($"#{p.Id} <b>{Html(p.Name)}</b> {(p.Enabled ? "▶️" : "⏸ disabled")}");
            sb.AppendLine($"Keyword: {Html(p.Keyword)}");
            sb.AppendLine(mine.Count == 0
                ? "Sources: <i>none yet</i>, add one with /addsource"
                : "Sources: " + string.Join(", ", mine.Select(SourceLabel)));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

[Command("profile", "filters")]
internal sealed class ShowProfileCommand(ProfileService profiles) : ProfileCommand
{
    protected override string UsageText => "/profile &lt;id&gt;";

    protected override async Task<string> RunAsync(CommandContext ctx, int profileId, string rest) =>
        Render(await profiles.GetAsync(ctx.ChatId, profileId, ctx.Ct), ProfileCard);

    private static string ProfileCard(ProfileDetails d)
    {
        var (p, list) = d;
        static string Show(List<string> l, string empty) => l.Count > 0 ? Html(FilterList.Join(l)) : $"<i>{empty}</i>";

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Html(p.Name)}</b> #{p.Id} {(p.Enabled ? "▶️ active" : "⏸ disabled")}");
        sb.AppendLine($"Keyword:    {Html(p.Keyword)}");
        sb.AppendLine($"Sources:    {(list.Count == 0 ? "<i>none</i>" : string.Join(", ", list.Select(SourceLabel)))}");
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
internal sealed class AddProfileCommand(ProfileService profiles) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
            return Usage($"/addprofile &lt;keyword&gt;  (1-{SourceCatalog.MaxKeywordLength} characters)\nExample: /addprofile full stack dev");

        return Render(await profiles.CreateAsync(ctx.ChatId, new ProfileDraft(ctx.Args), ctx.Ct), ProfileAdded);
    }
}

[Command("removeprofile")]
internal sealed class RemoveProfileCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/removeprofile &lt;id&gt;";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.RemoveAsync(ctx.ChatId, profileId, ctx.Ct);
    protected override string OkText(SearchProfile p) => ProfileRemoved(p);
}

[Command("name")]
internal sealed class RenameProfileCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/name &lt;id&gt; &lt;name&gt;";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.RenameAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(SearchProfile p) => ProfileRenamed(p);
}

[Command("keyword")]
internal sealed class SetKeywordCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/keyword &lt;id&gt; &lt;new keyword&gt;";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetKeywordAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(SearchProfile p) => KeywordChanged(p);
}

[Command("enable")]
internal sealed class EnableProfileCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/enable &lt;id&gt;";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetEnabledAsync(ctx.ChatId, profileId, true, ctx.Ct);
    protected override string OkText(SearchProfile p) => EnabledChanged(p);
}

[Command("disable")]
internal sealed class DisableProfileCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/disable &lt;id&gt;";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetEnabledAsync(ctx.ChatId, profileId, false, ctx.Ct);
    protected override string OkText(SearchProfile p) => EnabledChanged(p);
}