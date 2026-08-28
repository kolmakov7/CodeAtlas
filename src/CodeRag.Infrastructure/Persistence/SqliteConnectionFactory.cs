using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Фабрика SQLite соединений с PRAGMA journal_mode=WAL, synchronous=NORMAL,
/// foreign_keys=ON, busy_timeout=5000 и опциональной загрузкой vec0 extension.
/// Не падает если vec0.so отсутствует.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly ILogger<SqliteConnectionFactory> _logger;
    private readonly string? _explicitVecPath;
    private bool _vecAttempted;
    private bool _vecAvailable;
    private string? _resolvedVecPath;

    public SqliteConnectionFactory(string dbPath, ILogger<SqliteConnectionFactory> logger, string? vecExtensionPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("dbPath must not be empty.", nameof(dbPath));
        _dbPath = Path.GetFullPath(dbPath);
        _connectionString = $"Data Source={_dbPath}";
        _logger = logger;
        _explicitVecPath = string.IsNullOrWhiteSpace(vecExtensionPath) ? null : vecExtensionPath;
    }

    public string DbPath => _dbPath;
    public string ConnectionString => _connectionString;
    public bool IsVecAvailable => _vecAvailable;

    /// <summary>
    /// Создает открытое соединение с настроенными PRAGMA и попыткой загрузки vec0.
    /// </summary>
    public async Task<SqliteConnection> CreateAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await ConfigureConnectionAsync(conn, ct).ConfigureAwait(false);

        return conn;
    }

    /// <summary>Синхронная версия.</summary>
    public SqliteConnection Create()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ConfigureConnectionAsync(conn).GetAwaiter().GetResult();
        return conn;
    }

    private async Task ConfigureConnectionAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        // PRAGMA must be executed without transaction.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        cmd.CommandText = "PRAGMA synchronous=NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Optional vec0 extension.
        if (!_vecAttempted)
        {
            TryLoadVecExtension(conn);
            _vecAttempted = true;
        }
        else if (_vecAvailable && !string.IsNullOrEmpty(_resolvedVecPath))
        {
            // Each new connection needs to load extension again.
            TryLoadVecExtensionForConnection(conn);
        }
    }

    private void TryLoadVecExtension(SqliteConnection conn)
    {
        var path = ResolveVecPath();
        if (path is null)
        {
            _logger.LogDebug("sqlite-vec extension not found, vector search will use fallback. Searched paths include /tmp/vec0.so and nuget cache.");
            _vecAvailable = false;
            return;
        }

        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(path);
            _vecAvailable = true;
            _resolvedVecPath = path;
            _logger.LogInformation("Loaded sqlite-vec extension from {Path}", path);
        }
        catch (Exception ex)
        {
            _vecAvailable = false;
            _logger.LogWarning(ex, "Failed to load sqlite-vec extension from {Path}, vector search will use fallback", path);
        }
    }

    private void TryLoadVecExtensionForConnection(SqliteConnection conn)
    {
        if (string.IsNullOrEmpty(_resolvedVecPath)) return;
        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(_resolvedVecPath!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sqlite-vec extension on new connection from {Path}", _resolvedVecPath);
        }
    }

    private string? ResolveVecPath()
    {
        // 1. Explicit path
        if (!string.IsNullOrWhiteSpace(_explicitVecPath) && File.Exists(_explicitVecPath))
            return _explicitVecPath;

        // 2. Env var
        var env = Environment.GetEnvironmentVariable("SQLITE_VEC_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        // 3. Well-known /tmp locations
        var candidates = new[]
        {
            "/tmp/vec0.so",
            "/tmp/libvec0.so",
            "/tmp/sqlite-vec/vec0.so",
            Path.Combine(AppContext.BaseDirectory, "vec0.so"),
            Path.Combine(AppContext.BaseDirectory, "libvec0.so"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "vec0.so"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 4. Nuget cache search: ~/.nuget/packages/sqlite-vec
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var nugetRoot = Path.Combine(home, ".nuget", "packages");
            if (Directory.Exists(nugetRoot))
            {
                var vecDirs = Directory.GetDirectories(nugetRoot, "sqlite-vec*")
                    .Concat(Directory.GetDirectories(nugetRoot, "sqlite_vec*"))
                    .Concat(Directory.GetDirectories(nugetRoot, "*vec*"));
                foreach (var dir in vecDirs)
                {
                    var found = Directory.GetFiles(dir, "vec0.so", SearchOption.AllDirectories).FirstOrDefault();
                    if (found is not null && File.Exists(found)) return found;
                    found = Directory.GetFiles(dir, "libvec0.so", SearchOption.AllDirectories).FirstOrDefault();
                    if (found is not null && File.Exists(found)) return found;
                }
                // fallback: brute scan for vec0.so in nuget cache (limited)
                // avoid scanning entire cache due to performance — only check if above missed
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error searching nuget cache for vec0.so");
        }

        // 5. Try to locate via currently loaded assemblies path
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var files = Directory.GetFiles(baseDir, "vec0.so", SearchOption.AllDirectories);
            if (files.Length > 0) return files[0];
        }
        catch { /* ignore */ }

        return null;
    }
}
