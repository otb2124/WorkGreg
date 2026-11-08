using WorkGreg.Api;
using WorkGreg.Composition;
using WorkGreg.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddClock()
    .AddWorkGregOptions()
    .AddPersistence(builder.Configuration)
    .AddFetching()
    .AddApplicationServices()
    .AddTelegramBot()
    .AddPolling()
    .AddWorkGregApi(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.Migrate();
}

app.UseCors();
app.MapApi();

await app.RunAsync();