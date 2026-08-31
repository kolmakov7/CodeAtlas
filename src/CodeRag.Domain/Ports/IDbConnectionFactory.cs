using Microsoft.Data.Sqlite;

namespace CodeRag.Domain.Ports;

/// <summary>Порт для фабрики соединений — DIP, SRP. Infrastructure реализует.</summary>
public interface IDbConnectionFactory
{
    string DbPath { get; }
    string ConnectionString { get; }
    bool IsVecAvailable { get; }
    Task<SqliteConnection> CreateAsync(CancellationToken ct = default);
    SqliteConnection Create();
}
