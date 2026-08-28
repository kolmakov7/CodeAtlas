using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Применяет схему SQLite из embedded SQL миграций (DbUp-like).
/// Таблица schema_migrations(version) отслеживает применённые версии.
/// </summary>
public sealed class DbInitializer
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(SqliteConnectionFactory factory, ILogger<DbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.DbPath)!);

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        // Ensure schema_migrations exists (may already be in 001 but ensure early).
        await EnsureMigrationsTableAsync(conn, ct).ConfigureAwait(false);

        var migrations = LoadEmbeddedMigrations();

        foreach (var mig in migrations.OrderBy(m => m.Version))
        {
            var already = await IsAppliedAsync(conn, mig.Version, ct).ConfigureAwait(false);
            if (already)
            {
                _logger.LogDebug("Migration {Version} already applied", mig.Version);
                continue;
            }

            _logger.LogInformation("Applying migration {Version}", mig.Version);
            using var tx = conn.BeginTransaction();
            try
            {
                // Split on ; but keep FTS/vector definitions intact.
                // Use simple execution of whole script — SQLite handles multiple statements.
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = mig.Sql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow.ToString("o");
                await conn.ExecuteAsync(
                    "INSERT INTO schema_migrations(version, applied_at) VALUES (@v, @at);",
                    new { v = mig.Version, at = now },
                    transaction: tx);

                tx.Commit();
                _logger.LogInformation("Migration {Version} applied", mig.Version);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, "Failed to apply migration {Version}", mig.Version);
                throw;
            }
        }
    }

    private static async Task EnsureMigrationsTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at TEXT NOT NULL);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsAppliedAsync(SqliteConnection conn, string version, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = @v;";
        cmd.Parameters.AddWithValue("@v", version);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result) > 0;
    }

    private static IReadOnlyList<Migration> LoadEmbeddedMigrations()
    {
        var asm = typeof(DbInitializer).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var list = new List<Migration>();
        foreach (var name in names)
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            // Version from resource name or filename: expect 001_initial.sql
            var version = ExtractVersion(name);
            list.Add(new Migration(version, sql, name));
        }

        // Fallback: if no embedded resources (e.g. during dev without embedding), try file beside dll.
        if (list.Count == 0)
        {
            var baseDir = Path.GetDirectoryName(typeof(DbInitializer).Assembly.Location) ?? AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Persistence", "Migrations", "001_initial.sql");
            if (!File.Exists(candidate))
                candidate = Path.Combine(AppContext.BaseDirectory, "001_initial.sql");
            // also check project file path
            var projPath = Path.Combine("/home/shizzz/CodeAtlas/src/CodeRag.Infrastructure/Persistence/Migrations/001_initial.sql");
            if (File.Exists(projPath))
            {
                var sql = File.ReadAllText(projPath);
                list.Add(new Migration("001_initial", sql, projPath));
            }
            else if (File.Exists(candidate))
            {
                var sql = File.ReadAllText(candidate);
                list.Add(new Migration("001_initial", sql, candidate));
            }
        }

        return list;
    }

    private static string ExtractVersion(string resourceName)
    {
        // e.g. CodeRag.Infrastructure.Persistence.Migrations.001_initial.sql -> 001_initial
        var file = resourceName.Split('.').Reverse().Take(3).Reverse().ToArray();
        // Handles: 001_initial.sql -> ["001_initial","sql"]?
        // Simpler: take filename
        var name = Path.GetFileNameWithoutExtension(resourceName);
        // If resourceName contains dots, take last segment before .sql
        var parts = resourceName.Split('.');
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i] == "sql" && i > 0)
            {
                var maybe = parts[i - 1];
                // Check if previous is like 001_initial
                if (maybe.Contains("_") || maybe.All(char.IsDigit))
                    return maybe;
            }
        }
        // Fallback: extract 001_initial pattern via regex-ish
        var idx = resourceName.LastIndexOf("001_", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var sub = resourceName.Substring(idx);
            var dot = sub.IndexOf('.');
            if (dot > 0) sub = sub.Substring(0, dot);
            return sub;
        }
        return name;
    }

    private sealed record Migration(string Version, string Sql, string ResourceName);
}
