using Microsoft.EntityFrameworkCore;
using WorkGreg.Data;

namespace WorkGreg.Api;

public static class HealthEndpoint
{
    /// <summary>
    /// GET /health: 200 when the core is up and its database answers, 503 otherwise. No API key (monitors and the bot
    /// call it), and no details in the body.
    /// </summary>
    public static void MapHealth(this WebApplication app) => app.MapGet("/health", Check);

    private static async Task<IResult> Check(AppDb db, ILoggerFactory loggers, CancellationToken ct)
    {
        try
        {
            if (await db.Database.CanConnectAsync(ct))
                return Results.Ok(new { status = "ok" });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            loggers.CreateLogger("WorkGreg.Health").LogWarning(ex, "Health check: the database is not reachable");
        }

        return Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}