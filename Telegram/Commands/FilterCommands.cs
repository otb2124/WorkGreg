using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Commands;

[Command("include")]
internal sealed class IncludeCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/include &lt;profile&gt; kw1, kw2";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetIncludeAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(SearchProfile p) => IncludeSet(p);
}

[Command("exclude")]
internal sealed class ExcludeCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/exclude &lt;profile&gt; kw1, kw2";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetExcludeAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(SearchProfile p) => ExcludeSet(p);
}

[Command("companies")]
internal sealed class CompaniesCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/companies &lt;profile&gt; name1, name2";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetCompaniesAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(SearchProfile p) => CompaniesSet(p);
}

[Command("locations")]
internal sealed class LocationsCommand(ProfileService profiles) : ProfileEditCommand
{
    protected override string UsageText => "/locations &lt;profile&gt; city1, city2";
    protected override Task<Result<SearchProfile, Error>> EditAsync(CommandContext ctx, int profileId, string rest) =>
        profiles.SetLocationsAsync(ctx.ChatId, profileId, rest, ctx.Ct);
    protected override string OkText(SearchProfile p) => LocationsSet(p);
}

[Command("salary")]
internal sealed class SalaryCommand(ProfileService profiles) : ProfileCommand
{
    protected override string UsageText => "/salary &lt;profile&gt; &lt;positive number&gt;  (or off)";

    protected override async Task<string> RunAsync(CommandContext ctx, int profileId, string rest)
    {
        var arg = rest.Trim().ToLowerInvariant();
        int? salary = null;
        if (arg is not ("" or "off" or "clear"))
        {
            if (!int.TryParse(arg, out var parsed)) return Usage(UsageText);
            salary = parsed;
        }

        return Render(await profiles.SetMinSalaryAsync(ctx.ChatId, profileId, salary, ctx.Ct), SalarySet);
    }
}

[Command("remote")]
internal sealed class RemoteCommand(ProfileService profiles) : ProfileCommand
{
    protected override string UsageText => "/remote &lt;profile&gt; on|off";

    protected override async Task<string> RunAsync(CommandContext ctx, int profileId, string rest)
    {
        var arg = rest.Trim().ToLowerInvariant();
        if (arg is not ("on" or "off")) return Usage(UsageText);

        return Render(await profiles.SetRemoteOnlyAsync(ctx.ChatId, profileId, arg == "on", ctx.Ct), RemoteSet);
    }
}