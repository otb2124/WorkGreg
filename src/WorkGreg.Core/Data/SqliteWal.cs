using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Data;

public static class SqliteWal
{
    /// <summary>
    /// Switches the database to write-ahead logging, so the poll cycle's writes and the API's reads/writes stop blocking
    /// each other (readers no longer wait for a writer). The mode is stored in the database file, so this is a one-time
    /// switch; running it at every startup is harmless. Call it once at startup, after migrations, while nothing else
    /// has the database open.
    /// </summary>
    public static async Task EnableWalAsync(this AppDb db, ILogger log, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";

            // The pragma answers with the mode that is now active ("wal"; "memory" for an in-memory database).
            var mode = Convert.ToString(await command.ExecuteScalarAsync(ct));

            if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                log.LogInformation("SQLite journal mode: WAL");
            else
                log.LogWarning("SQLite stayed in journal mode '{Mode}' instead of WAL", mode);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}