using System.Data;
using CodeRag.Domain.Ports;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Фабрика SQLite соединений с PRAGMA journal_mode=WAL, synchronous=NORMAL,
/// foreign_keys=ON, busy_timeout=60000 и опциональной загрузкой vec0 extension.
/// Не падает если vec0.so отсутствует.
/// Layout: main = &lt;db&gt;, chunks = &lt;name&gt;-chunks.db, vec = &lt;name&gt;-vec.db (same directory).
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _chunksConnectionString;
    private readonly string _vecConnectionString;
    private readonly string _dbPath;
    private readonly string _chunksDbPath;
    private readonly string _vecDbPath;
    private readonly ILogger<SqliteConnectionFactory> _logger;
    private readonly string? _explicitVecPath;
    private int _vecAttempted; // 0 = not attempted, 1 = attempted - Interlocked
    private bool _vecAvailable;
    private string? _resolvedVecPath;
    private readonly object _vecLock = new();

    public SqliteConnectionFactory(string dbPath, ILogger<SqliteConnectionFactory> logger, string? vecExtensionPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("dbPath must not be empty.", nameof(dbPath));
        _dbPath = Path.GetFullPath(dbPath);
        _connectionString = $"Data Source={_dbPath}";
        _chunksDbPath = ResolveSiblingDbPath(_dbPath, "-chunks");
        _chunksConnectionString = $"Data Source={_chunksDbPath}";
        _vecDbPath = ResolveSiblingDbPath(_dbPath, "-vec");
        _vecConnectionString = $"Data Source={_vecDbPath}";
        _logger = logger;
        _explicitVecPath = string.IsNullOrWhiteSpace(vecExtensionPath) ? null : vecExtensionPath;
    }

    public string Provider => "sqlite";
    public string DataDir => Path.GetDirectoryName(_dbPath) ?? "";
    public string DbPath => _dbPath;
    public string ChunksDbPath => _chunksDbPath;

    /// <summary>Separate vector database: vec0 tables live in &lt;db&gt;-vec.db next to the main DB.</summary>
    public string VecDbPath => _vecDbPath;

    public string ConnectionString => _connectionString;
    public bool IsVecAvailable => _vecAvailable;

    private static string ResolveSiblingDbPath(string dbPath, string suffix)
    {
        var dir = Path.GetDirectoryName(dbPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(dbPath);
        if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dbPath);
        return Path.Combine(dir, name + suffix + ".db");
    }

    public async Task ProvisionAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DataDir);
    }

    /// <summary>
    /// Создает открытое соединение с настроенными PRAGMA и попыткой загрузки vec0.
    /// </summary>
    public async Task<IDbConnection> CreateAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await ConfigureConnectionAsync(conn, ct).ConfigureAwait(false);

        return conn;
    }

    /// <summary>Opens a connection to the separate chunks database (chunks + chunks_fts).</summary>
    public async Task<IDbConnection> CreateChunksAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DataDir);
        var conn = new SqliteConnection(_chunksConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await ConfigureConnectionAsync(conn, ct).ConfigureAwait(false);

        return conn;
    }

    /// <summary>
    /// Opens a connection to the separate vector database (vec0 tables) with the same PRAGMAs.
    /// </summary>
    public async Task<IDbConnection> CreateVecAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DataDir);
        var conn = new SqliteConnection(_vecConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await ConfigureConnectionAsync(conn, ct).ConfigureAwait(false);

        return conn;
    }

    /// <summary>Синхронная версия.</summary>
    public IDbConnection Create()
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

        // 300s: tolerate contention from parallel index writers (2 repos) + polling sync + probes.
        // Rebuild cleanup (vec + trigram-FTS repo deletes) can hold the write lock for minutes.
        cmd.CommandText = "PRAGMA busy_timeout=300000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Optional vec0 extension — thread-safe via Interlocked + lock.
        if (Interlocked.CompareExchange(ref _vecAttempted, 1, 0) == 0)
        {
            lock (_vecLock)
            {
                TryLoadVecExtension(conn);
            }
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
