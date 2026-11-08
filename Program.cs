using WorkGreg.Api;
using WorkGreg.Composition;
using WorkGreg.Data;
using WorkGreg.Domain;
using WorkGreg.Fetching;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddClock()
    .AddWorkGregOptions()
    .AddSourceCatalog(builder.Configuration)   // reads + validates providers.json; throws with every problem listed
    .AddPersistence(builder.Configuration)
    .AddFetching()
    .AddApplicationServices()
    .AddTelegramBot()
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