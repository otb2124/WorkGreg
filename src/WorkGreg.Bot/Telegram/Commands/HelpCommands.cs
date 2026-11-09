using WorkGreg.Client;

namespace WorkGreg.Telegram.Commands;

[Command("help", MenuText = "List of text commands", MenuOrder = 3)]
internal sealed class HelpCommand(IWorkGregClient client) : TextCommand
{
    protected override async Task<string> RunAsync(CommandContext ctx)
    {
        var meta = await client.GetMetaAsync(ctx.Ct);

        return $"""
            <b>Job watcher</b>
            I check your profiles every hour and message you about vacancies that match them.
            Tip: /menu opens the button menu.

            A <b>profile</b> is one job hunt: a keyword, its own filters and its own sources.
            Commands take the profile id (see /profiles).

            <b>Profiles</b>
            /profiles
            /addprofile &lt;keyword&gt;       (e.g. /addprofile full stack dev)
            /profile &lt;id&gt;               (details and filters)
            /name &lt;id&gt; &lt;name&gt;
            /keyword &lt;id&gt; &lt;keyword&gt;
            /enable &lt;id&gt;  /disable &lt;id&gt;
            /removeprofile &lt;id&gt;

            <b>Sources of a profile</b>  ({meta.ProviderIds})
            /addsource &lt;id&gt; &lt;source&gt;
            /removesource &lt;id&gt; &lt;source&gt;

            <b>Filters of a profile</b>
            /include &lt;id&gt; kw1, kw2       (match ANY; no list = clear)
            /exclude &lt;id&gt; kw1, kw2       (against title only)
            /companies &lt;id&gt; name1, name2
            /locations &lt;id&gt; city1, city2
            /salary &lt;id&gt; 1000            (jobs without reported salary are dropped; /salary &lt;id&gt; off)
            /remote &lt;id&gt; on|off

            <b>Delivery</b> (all profiles)
            /delivery instant
            /delivery digest 9     (hour in UTC, 0-23)

            <b>Control</b>
            /check  (run now)
            /resend  (mark all stored vacancies unsent; the next /check sends all matches again)
            /pause, /resume
            /stop  (cancel a check that is running right now)
            """;
    }
}