using Microsoft.Data.Sqlite;

namespace CodeRag.Domain.Ports;

/// <summary>Порт для фабрики соединений — DIP, SRP. Infrastructure реализует.</summary>
public interface IDbConnectionFactory
{
    string DbPath { get; }
    string ConnectionString { get; }
    bool IsVecAvailable { get; }

    /// <summary>Path of the separate vector database (vec0 tables, e.g. coderag-vec.db).</summary>
    string VecDbPath { get; }

    Task<SqliteConnection> CreateAsync(CancellationToken ct = default);
    SqliteConnection Create();

    /// <summary>Opens a connection to the separate vector database with the same PRAGMAs.</summary>
    Task<SqliteConnection> CreateVecAsync(CancellationToken ct = default);
}
