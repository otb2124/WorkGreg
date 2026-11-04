using System.Text.Json.Serialization;
using WorkGreg.Api;
using WorkGreg.Data;
using WorkGreg.Fetching;
using WorkGreg.Services;
using WorkGreg.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using global::Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<PollOptions>(builder.Configuration.GetSection("Polling"));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));

var apiOptions = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
if (string.IsNullOrWhiteSpace(apiOptions.ApiKey))
    throw new InvalidOperationException("Api:ApiKey is not set. Use user-secrets or the Api__ApiKey env var.");

var dbPath = builder.Configuration["Database:Path"] ?? "WorkGreg.db";
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddHttpClient("fetch", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("WorkGreg/1.0 (personal job alerts)");
});

// Fetch strategies: add new IJobFetcher implementations here.
builder.Services.AddSingleton<IJobFetcher, FeedFetcher>();
builder.Services.AddSingleton<IJobFetcher, HtmlFetcher>();
builder.Services.AddSingleton<IJobFetcher, BrowserFetcher>();
builder.Services.AddSingleton<FetcherRegistry>();

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var token = sp.GetRequiredService<IOptions<BotOptions>>().Value.Token;
    if (string.IsNullOrWhiteSpace(token))
        throw new InvalidOperationException("Bot:Token is not set. Use user-secrets or the Bot__Token env var.");
    return new TelegramBotClient(token);
});
builder.Services.AddSingleton<INotifier, TelegramNotifier>();
builder.Services.AddSingleton<PollRunner>();
builder.Services.AddScoped<CommandHandler>();

builder.Services.AddHostedService<BotService>();
builder.Services.AddSingleton<PendingInputStore>();
builder.Services.AddScoped<MenuHandler>();

builder.Services.AddHostedService<PollingWorker>();

// API: enums as strings ("feed" / "Feed") instead of numbers.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (apiOptions.AllowedOrigins.Length > 0)
        p.WithOrigins(apiOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.Migrate();
}

app.UseCors();
app.MapApi();

await app.RunAsync();