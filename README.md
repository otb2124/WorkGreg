# WorkGreg

Hourly job-vacancy watcher with a Telegram bot. Each user creates one or more **profiles** (one per job hunt, e.g. "full stack dev", "it support"). A profile has its own keyword, its own filters and its own sources; only *new* matching vacancies are sent.

## Run

1. Create a bot with @BotFather and copy the token.
2. Configure secrets (do not commit them):
   ```
   dotnet user-secrets init
   dotnet user-secrets set "Bot:Token" "123456:ABC..."
   dotnet user-secrets set "Api:ApiKey" "a-long-random-string"
   ```
   or set the env vars `Bot__Token` and `Api__ApiKey`. The app refuses to start when either is missing.
3. Decide who may use the bot (`Bot:Access` in `appsettings.json`):
   - `"Open"`: any Telegram chat can use the bot.
   - `"Allowlist"`: only the ids in `Bot:AllowedChatIds` (e.g. `[123456789]`). The log prints the chat id of an unauthorized chat that writes to the bot. An empty allowlist is rejected at startup: it does **not** mean "anyone".
4. `dotnet run`, then message your bot.
5. In Telegram:
   ```
   /addprofile full stack dev
   /addsource 1 remotive
   /addsource 1 wwr
   /include 1 .NET, C#, Angular, full stack
   /exclude 1 senior, lead, principal
   /salary 1 1000
   /locations 1 Zagreb, Europe
   /addprofile it support
   /addsource 2 remotive
   /salary 2 800
   /check
   ```

DOU has RSS for vacancy searches (use the RSS link on the filtered vacancies page and verify it still exists). Other boards: look for RSS/Atom links or saved-search feeds first.

## Configuration

| Key | Meaning |
| --- | --- |
| `Bot:Token` | Telegram bot token (required) |
| `Bot:Access` | `Open` or `Allowlist` (default `Allowlist`) |
| `Bot:AllowedChatIds` | Chat ids allowed when `Access` is `Allowlist` |
| `Api:ApiKey` | Required. Clients send it in the `X-Api-Key` header |
| `Api:AllowedOrigins` | Browser origins allowed by CORS (empty = none) |
| `Polling:IntervalMinutes` | Time between scheduled cycles (default 60) |
| `Polling:MaxMessagesPerRun` | Max vacancies per user per cycle (default 20) |
| `Polling:SendDelayMilliseconds` | Pause between messages (default 300) |

Options are validated at startup; a wrong value stops the app with a message saying what to fix.

## Profiles

- `SearchProfile` = keyword + filters (include, exclude, companies, locations, min salary, remote) + sources. `Source.SearchProfileId` points to it; the keyword is not stored on the source anymore.
- Delivery mode (instant/digest) and pause are per user and apply to all profiles (`UserProfile`).
- A disabled profile is not fetched and delivers nothing; its stored jobs wait until it is enabled.
- The same vacancy found by two profiles (or two boards) is delivered once (company+title hash, per user). Vacancies from a source that exposes no company (RSS) are only matched within that source, so two employers with the same title are not merged.
- Sources that don't depend on the keyword in their URL (Remotive, WWR) are fetched once per poll even if several profiles use them.

## How it works

- `Fetching/IJobFetcher` is the extension point, one implementation per `FetchType`. `Feed` and `Api` are implemented; `Html` and `Browser` are registered placeholders (`IsAvailable = false`), so the bot already knows about them but refuses to create sources of that type.
- `Services/PollRunner` does a cycle: fetch all sources, store unseen jobs, apply each user's filters, send, mark delivered. State is saved per message, so restarts don't create duplicates. Cycles run one at a time.
- A new source's current vacancies that match the profile's filters are sent too, up to `MaxMessagesPerRun` per cycle; the rest follow on the next cycles.
- 3 consecutive failures of a source trigger one alert message. A timeout counts as a failure of that source only.
- `/check` and `/stop` act on **your own** manual check. The scheduled hourly check covers everybody and can't be stopped from chat.
- If a user blocks the bot (Telegram answers 403), their profiles are disabled so we stop fetching for them; they can re-enable them from the menu. A Telegram rate limit (429) is retried a couple of times, then that user is skipped until the next cycle. A message Telegram refuses (400) is skipped without blocking the vacancies behind it. None of this affects other users.
- `Source.ConfigJson` is reserved for per-source settings, so adding fetchers needs no schema change.

## Adding a fetcher

1. Implement `IJobFetcher` (return `FetchedJob`s), set `IsAvailable => true`.
2. Register it in `AddFetching()` in `Composition/ServiceCollectionExtensions.cs`.

## Tests

```
dotnet test WorkGreg.Tests
```

The tests pin the current filtering and keyword behaviour, check the provider catalog for mistakes (duplicate ids, missing `title`/`url` mappings, bad URLs), and exercise `PollRunner` with a fake fetcher and notifier on in-memory SQLite.

## Notes

- Targets .NET 8. The database is created and updated by EF migrations on startup.
- Exclude keywords match the title only; include keywords match title, company, location and description. Both are plain substring matches (the profile keyword uses whole-word matching).