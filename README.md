# WorkGreg

Hourly job-vacancy watcher with a Telegram bot. Each user builds a pool of sources (with a fetch type per source) and their own filters; only *new* matching vacancies are sent.

## Run

1. Create a bot with @BotFather and copy the token.
2. Configure secrets (do not commit the token):
   ```
   dotnet user-secrets init
   dotnet user-secrets set "Bot:Token" "123456:ABC..."
   ```
   or set the env var `Bot__Token`.
3. `dotnet run`, then message your bot. The log prints your chat id when an unauthorized chat writes to it; put it into `Bot:AllowedChatIds` (e.g. `[123456789]`) and restart.
4. In Telegram:
   ```
   /addsource feed https://example.com/vacancies.rss My feed
   /include .NET, C#, Angular, full stack
   /exclude senior, lead, principal
   /remote on
   /check
   ```

DOU has RSS for vacancy searches (use the RSS link on the filtered vacancies page and verify it still exists). Other boards: look for RSS/Atom links or saved-search feeds first.

## How it works

- `Fetching/IJobFetcher` is the extension point, one implementation per `FetchType`. `Feed` is implemented; `Html` and `Browser` are registered placeholders (`IsAvailable = false`), so the bot already knows about them but refuses to create sources of that type.
- `Services/PollRunner` does a cycle: fetch all sources, store unseen jobs, apply each user's filters, send, mark delivered. State is saved per message, so restarts don't create duplicates.
- First successful fetch of a source is a baseline: existing vacancies are recorded, not sent.
- Same vacancy on several boards is de-duplicated by a company+title hash (only useful once fetchers fill `Company`).
- 3 consecutive failures of a source trigger one alert message.
- `Source.ConfigJson` is reserved for per-source settings (e.g. CSS selectors for the Html fetcher), so adding fetchers needs no schema change.

## Adding a fetcher

1. Implement `IJobFetcher` (return `FetchedJob`s), set `IsAvailable => true`.
2. Make sure it's registered in `Program.cs`.

## Notes

- Targets .NET 8. Uses `EnsureCreated`; switch to EF migrations once the schema stabilizes.
- Exclude keywords match the title only; include keywords match title, company, location and description.
