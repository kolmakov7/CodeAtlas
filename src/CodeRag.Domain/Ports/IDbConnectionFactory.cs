using System.Data;

namespace CodeRag.Domain.Ports;

/// <summary>Порт для фабрики соединений — DIP, SRP. Infrastructure реализует.</summary>
public interface IDbConnectionFactory
{
    /// <summary>"sqlite" | "postgresql". Статический выбор на старте, без hot-switching.</summary>
    string Provider { get; }

    /// <summary>Каталог данных (SQLite: директория с .db-файлами; PostgreSQL: пустая строка).</summary>
    string DataDir { get; }

    /// <summary>Идентификатор main БД (SQLite: путь к файлу; PostgreSQL: имя базы).</summary>
    string DbPath { get; }

    /// <summary>Идентификатор chunks БД (chunks + chunks_fts).</summary>
    string ChunksDbPath { get; }

    /// <summary>Идентификатор vector БД (vec0 / pgvector).</summary>
    string VecDbPath { get; }

    string ConnectionString { get; }
    bool IsVecAvailable { get; }

    Task<IDbConnection> CreateAsync(CancellationToken ct = default);
    IDbConnection Create();

    /// <summary>Opens a connection to the separate chunks database.</summary>
    Task<IDbConnection> CreateChunksAsync(CancellationToken ct = default);

    /// <summary>Opens a connection to the separate vector database.</summary>
    Task<IDbConnection> CreateVecAsync(CancellationToken ct = default);

    /// <summary>Creates the backing storage (directories / PostgreSQL databases) if missing.</summary>
    Task ProvisionAsync(CancellationToken ct = default);
}
