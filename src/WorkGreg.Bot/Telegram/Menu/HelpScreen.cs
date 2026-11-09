using static WorkGreg.Telegram.MenuKit;

namespace WorkGreg.Telegram.Menu;

[Handles(CallbackAction.Help)]
internal sealed class HelpScreen(PendingInputStore pending) : NavigationHandler(pending)
{
    protected override Task<Screen> OpenAsync(CallbackContext ctx) => Task.FromResult(Render());

    public static Screen Render()
    {
        const string text = """
            <b>❓ Help</b>

            I check your profiles every hour and message you about vacancies that match them.

            A 📂 <b>profile</b> is one job hunt, for example "full stack dev" or "IT support". Each profile has:
            • 🔎 a <b>keyword</b> to search for
            • 📡 its own <b>sources</b> (job boards)
            • 🎯 its own <b>filters</b>: include/exclude words, companies, locations, min salary, remote

            📬 <b>Delivery</b> (instant or one daily digest) and ⏸ pause apply to all profiles.
            🔁 <b>Resend</b> marks everything as unsent, so the next check sends all matches again.

            A new source's current vacancies are sent too (the ones matching the profile's filters), up to 20 per hourly run.
            If the same vacancy turns up in several profiles or boards, you get it once.

            Slash commands still work too: /menu /profiles /check /pause /resume /resend /help
            """;
        return new Screen(text, Kb(Row(Btn("⬅ Back", CallbackData.Home))));
    }
}