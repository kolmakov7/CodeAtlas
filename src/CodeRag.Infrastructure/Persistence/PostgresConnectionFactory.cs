using System.Data;
using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Фабрика PostgreSQL соединений. Одна логическая схема = отдельная база:
/// main = &lt;Database&gt; из connection string, chunks = &lt;Database&gt;_chunks, vec = &lt;Database&gt;_vec.
/// Базы создаются автоматически при ProvisionAsync (через maintenance-подключение к базе postgres).
/// </summary>
public sealed class PostgresConnectionFactory : IDbConnectionFactory
{
    private readonly string _mainConnectionString;
    private readonly string _chunksConnectionString;
    private readonly string _vecConnectionString;
    private readonly string _maintenanceConnectionString;
    private readonly string _mainDbName;
    private readonly string _chunksDbName;
    private readonly string _vecDbName;
    private readonly ILogger<PostgresConnectionFactory> _logger;
    private readonly object _vecLock = new();
    private int _vecProbed;
    private bool _vecAvailable;

    public PostgresConnectionFactory(string connectionString, ILogger<PostgresConnectionFactory> logger)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Postgres connection string must not be empty.", nameof(connectionString));

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 10,
        };

        if (string.IsNullOrWhiteSpace(builder.Database) || builder.Database == "postgres")
            throw new ArgumentException(
                "Postgres connection string must specify an explicit Database (maintenance DB 'postgres' is reserved for provisioning).",
                nameof(connectionString));

        _mainDbName = SanitizeDbName(builder.Database);
        _chunksDbName = SanitizeDbName(_mainDbName + "_chunks");
        _vecDbName = SanitizeDbName(_mainDbName + "_vec");

        builder.Database = _mainDbName;
        _mainConnectionString = builder.ConnectionString;

        builder.Database = _chunksDbName;
        _chunksConnectionString = builder.ConnectionString;

        builder.Database = _vecDbName;
        _vecConnectionString = builder.ConnectionString;

        builder.Database = "postgres";
        _maintenanceConnectionString = builder.ConnectionString;

        _logger = logger;
    }

    public string Provider => "postgresql";
    public string DataDir => "";
    public string DbPath => _mainDbName;
    public string ChunksDbPath => _chunksDbName;
    public string VecDbPath => _vecDbName;
    public string ConnectionString => _mainConnectionString;

    public bool IsVecAvailable
    {
        get
        {
            if (Interlocked.CompareExchange(ref _vecProbed, 1, 0) == 0)
            {
                lock (_vecLock)
                {
                    _vecAvailable = ProbeVecExtension();
                }
            }

            return _vecAvailable;
        }
    }

    public Task ProvisionAsync(CancellationToken ct = default) => EnsureDatabasesAsync(ct);

    /// <summary>Ensures all three databases exist. Call at startup (idempotent).</summary>
    public async Task EnsureDatabasesAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        foreach (var dbName in new[] { _mainDbName, _chunksDbName, _vecDbName })
        {
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name)",
                new { name = dbName }).ConfigureAwait(false);
            if (exists) continue;

            await conn.ExecuteAsync($"CREATE DATABASE {QuoteIdentifier(dbName)};").ConfigureAwait(false);
            _logger.LogInformation("Created PostgreSQL database {Database}", dbName);
        }
    }

    public async Task<IDbConnection> CreateAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(_mainConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public async Task<IDbConnection> CreateChunksAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(_chunksConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public async Task<IDbConnection> CreateVecAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(_vecConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public IDbConnection Create()
    {
        var conn = new NpgsqlConnection(_mainConnectionString);
        conn.Open();
        return conn;
    }

    private bool ProbeVecExtension()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_mainConnectionString) { Database = "postgres", Timeout = 5 };
            using var conn = new NpgsqlConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_available_extensions WHERE name = 'vector')";
            var result = cmd.ExecuteScalar();
            var ok = result is true;
            if (!ok)
                _logger.LogWarning("pgvector extension is not available on this PostgreSQL server; vector search will be disabled.");
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe pgvector availability; vector search will be disabled.");
            return false;
        }
    }

    private static string SanitizeDbName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var result = new string(chars);
        if (result.Length == 0 || char.IsDigit(result[0])) result = "coderag_" + result;
        return result.Length > 60 ? result[..60] : result;
    }

    private static string QuoteIdentifier(string name)
    {
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}
