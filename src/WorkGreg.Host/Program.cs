using WorkGreg.Api;
using WorkGreg.Composition;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Secrets (Bot:Token, Api:ApiKey, ...) live in secrets.json next to the app and are never committed.
// Looked for in the content root (the project folder in development, the working directory when deployed)
// and next to the executable. Optional: the app still starts without it if the values come from elsewhere.
// A later file overrides an earlier one; environment variables are added again afterwards so they still win.
builder.Configuration
    .AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "secrets.json"), optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "secrets.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services
    .AddClock()
    .AddWorkGregOptions()
    .AddSourceCatalog(builder.Configuration)   // reads + validates providers.json; throws with every problem listed
    .AddPersistence(builder.Configuration)
    .AddFetching()
    .AddApplicationServices()
    .AddTelegramNotifier()                     // core: outgoing vacancy messages
    .AddTelegramBot()                          // bot project: in step 4 this moves to the bot's own Program.cs
    .AddPolling()
    .AddWorkGregApi(builder.Configuration);

var app = builder.Build();

// Every provider type in the catalog needs a registered fetcher.
CatalogValidator.RequireFetchers(
    SourceCatalog.All,
    app.Services.GetRequiredService<FetcherRegistry>().All.Select(f => f.Type));

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.Migrate();
}

app.UseCors();
app.MapApi();

await app.RunAsync();