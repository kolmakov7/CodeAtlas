using System.Data;
using System.Reflection;
using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Применяет схему из embedded SQL миграций (DbUp-like) для каждой логической БД:
/// Main (Migrations/{provider}/Main) и Chunks (Migrations/{provider}/Chunks).
/// Таблица schema_migrations(version) в каждой БД отслеживает применённые версии.
/// Provider = "Sqlite" | "Postgres" (выбран статически на старте).
/// </summary>
public sealed class DbInitializer
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(IDbConnectionFactory factory, ILogger<DbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Ensure backing storage exists (SQLite directories / PostgreSQL databases).
        await _factory.ProvisionAsync(ct).ConfigureAwait(false);

        var provider = SqlDialect.IsPostgres(_factory) ? "Postgres" : "Sqlite";

        await MigrateAsync("Main", () => _factory.CreateAsync(ct), provider, ct).ConfigureAwait(false);
        await EnsureProjectsRepositoryNullableAsync(provider, ct).ConfigureAwait(false);
        await MigrateAsync("Chunks", () => _factory.CreateChunksAsync(ct), provider, ct).ConfigureAwait(false);

        WarnAboutLegacyLayout(provider, ct);
    }

    private async Task EnsureProjectsRepositoryNullableAsync(string provider, CancellationToken ct)
    {
        if (!string.Equals(provider, "Sqlite", StringComparison.Ordinal)) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var columns = (await conn.QueryAsync<(long cid, string name, string type, long notnull)>(
            "PRAGMA table_info(projects);").ConfigureAwait(false)).ToList();
        var repoColumn = columns.FirstOrDefault(c => c.name == "repository_id");
        if (string.IsNullOrEmpty(repoColumn.name) || repoColumn.notnull == 0) return;

        _logger.LogInformation("Rebuilding projects table to allow empty projects (repository_id nullable)");
        await conn.ExecuteAsync("PRAGMA foreign_keys=OFF;").ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync("DROP TABLE IF EXISTS projects_new;", transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync(@"
CREATE TABLE projects_new (
    id TEXT PRIMARY KEY,
    repository_id TEXT REFERENCES repositories(id) ON DELETE SET NULL,
    name TEXT,
    path TEXT,
    language TEXT
);
INSERT INTO projects_new(id, repository_id, name, path, language)
SELECT id, repository_id, name, path, language FROM projects;
DROP TABLE projects;
ALTER TABLE projects_new RENAME TO projects;", transaction: tx).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            await conn.ExecuteAsync("PRAGMA foreign_keys=ON;").ConfigureAwait(false);
        }
        var violations = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false);
        if (violations > 0)
            _logger.LogWarning("projects rebuild left {Count} foreign-key violations", violations);
    }

    private async Task MigrateAsync(string kind, Func<Task<IDbConnection>> open, string provider, CancellationToken ct)
    {
        using var conn = await open().ConfigureAwait(false);
        await EnsureMigrationsTableAsync(conn, ct).ConfigureAwait(false);

        var migrations = LoadEmbeddedMigrations(provider, kind);
        if (migrations.Count == 0)
        {
            _logger.LogWarning("No embedded migrations found for {Provider}/{Kind} — skipping", provider, kind);
            return;
        }

        foreach (var mig in migrations.OrderBy(m => m.Version, StringComparer.Ordinal))
        {
            var already = await IsAppliedAsync(conn, mig.Version, ct).ConfigureAwait(false);
            if (already)
            {
                _logger.LogDebug("{Kind} migration {Version} already applied", kind, mig.Version);
                continue;
            }

            _logger.LogInformation("Applying {Kind} migration {Version} ({Provider})", kind, mig.Version, provider);
            using var tx = conn.BeginTransaction();
            try
            {
                // Whole-script execution: SQLite and Npgsql both support multiple statements
                // in one command (no parameters in migration scripts).
                await conn.ExecuteAsync(mig.Sql, transaction: tx).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow.ToString("o");
                await conn.ExecuteAsync(
                    "INSERT INTO schema_migrations(version, applied_at) VALUES (@v, @at);",
                    new { v = mig.Version, at = now },
                    transaction: tx).ConfigureAwait(false);

                tx.Commit();
                _logger.LogInformation("{Kind} migration {Version} applied", kind, mig.Version);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, "Failed to apply {Kind} migration {Version} ({Provider})", kind, mig.Version, provider);
                throw;
            }
        }
    }

    /// <summary>
    /// Alpha: the storage layout changed (chunks + vector split into separate databases,
    /// vector_entries.repository_id added). Old single-file layouts are NOT migrated
    /// automatically — surface a loud warning instead of corrupting the old data.
    /// </summary>
    private async Task WarnAboutLegacyLayout(string provider, CancellationToken ct)
    {
        if (provider != "Sqlite") return;

        try
        {
            using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
            var legacyChunks = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('chunks','chunks_fts');").ConfigureAwait(false);
            var hasLegacyVectorEntries = false;
            var veSql = await conn.ExecuteScalarAsync<string?>(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='vector_entries';").ConfigureAwait(false);
            if (veSql is not null && !veSql.Contains("repository_id", StringComparison.Ordinal))
                hasLegacyVectorEntries = true;

            var hasMigrations = await IsAppliedAsync(conn, "001_initial", ct).ConfigureAwait(false);
            if (hasMigrations) return; // properly initialized DB

            var hasAnyTable = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name != 'schema_migrations';").ConfigureAwait(false);
            if (hasAnyTable == 0) return; // brand-new empty database

            if (legacyChunks > 0 || hasLegacyVectorEntries)
            {
                _logger.LogWarning(
                    "Legacy single-file CodeRag database detected at {Path}. The storage layout has changed: " +
                    "chunks + FTS moved to {ChunksDb} and vector tables move to {VecDb}, and vector_entries gained repository_id. " +
                    "Old data is NOT migrated automatically (alpha) — delete the old .db files to start fresh.",
                    _factory.DbPath, _factory.ChunksDbPath, _factory.VecDbPath);
            }
            else
            {
                _logger.LogWarning(
                    "Pre-existing database at {Path} without schema_migrations — applying the consolidated initial schema on top of it",
                    _factory.DbPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy layout check failed");
        }
    }

    private static async Task EnsureMigrationsTableAsync(IDbConnection conn, CancellationToken ct)
    {
        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at TEXT NOT NULL);").ConfigureAwait(false);
    }

    private static async Task<bool> IsAppliedAsync(IDbConnection conn, string version, CancellationToken ct)
    {
        var result = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM schema_migrations WHERE version = @v;", new { v = version }).ConfigureAwait(false);
        return result > 0;
    }

    private static IReadOnlyList<Migration> LoadEmbeddedMigrations(string provider, string kind)
    {
        var asm = typeof(DbInitializer).Assembly;
        var prefix = $"CodeRag.Infrastructure.Persistence.Migrations.{provider}.{kind}.";

        var list = new List<Migration>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            list.Add(new Migration(ExtractVersion(name), sql, name));
        }

        // Fallback: file beside the assembly (dev scenarios without embedded resources).
        if (list.Count == 0)
        {
            var baseDir = Path.GetDirectoryName(typeof(DbInitializer).Assembly.Location) ?? AppContext.BaseDirectory;
            var dir = Path.Combine(baseDir, "Persistence", "Migrations", provider, kind);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
                    list.Add(new Migration(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file));
            }
        }

        return list;
    }

    private static string ExtractVersion(string resourceName)
    {
        // Embedded resource names use dots as separators:
        //   CodeRag.Infrastructure.Persistence.Migrations.Sqlite.Main.001_initial.sql -> 001_initial
        // File fallback paths use real separators and are handled by GetFileNameWithoutExtension.
        if (resourceName.Contains('/') || resourceName.Contains('\\'))
            return Path.GetFileNameWithoutExtension(resourceName);

        var dot = resourceName.LastIndexOf('.'); // before "sql"
        var beforeExt = dot > 0 ? resourceName[..dot] : resourceName;
        var fileDot = beforeExt.LastIndexOf('.'); // before the version filename
        return fileDot >= 0 ? beforeExt[(fileDot + 1)..] : beforeExt;
    }

    private sealed record Migration(string Version, string Sql, string ResourceName);
}
