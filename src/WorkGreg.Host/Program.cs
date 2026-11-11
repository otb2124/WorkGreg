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
    .AddPolling()
    .AddWorkGregApi(builder.Configuration);

var app = builder.Build();

// Every provider type in the catalog needs a registered fetcher.
CatalogValidator.RequireFetchers(
    SourceCatalog.All,
    app.Services.GetRequiredService<FetcherRegistry>().All.Select(f => f.Type));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.Migrate();
    await db.EnableWalAsync(app.Logger);   // polling and API writes overlap: readers must not wait for the writer
}

app.UseCors();
app.MapHealth();   // GET /health, no API key
app.MapApi();

await app.RunAsync();