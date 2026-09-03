using CodeRag.Domain.Ports;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>Тонкий dialect-helper: отличия SQLite/PostgreSQL, которые всплывают в общем коде.</summary>
public static class SqlDialect
{
    public static bool IsPostgres(IDbConnectionFactory factory) => factory.Provider == "postgresql";

    /// <summary>Unique-constraint violation: SQLite error 19 / PostgreSQL SQLSTATE 23505.</summary>
    public static bool IsUniqueViolation(IDbConnectionFactory factory, Exception ex)
    {
        return ex switch
        {
            SqliteException s when s.SqliteErrorCode == 19 => true,
            PostgresException p when p.SqlState == "23505" => true,
            _ => false,
        };
    }

    /// <summary>Кавычки для идентификаторов: PostgreSQL требует двойные кавычки
    /// (например, для зарезервированного namespace), SQLite принимает как есть.</summary>
    public static string QuoteIdent(IDbConnectionFactory factory, string ident) =>
        IsPostgres(factory) ? "\"" + ident + "\"" : ident;

    /// <summary>Case-insensitive LIKE: SQLite — <c>LIKE ... COLLATE NOCASE</c>, PG — <c>ILIKE</c>.</summary>
    public static string NoCaseLike(IDbConnectionFactory factory, string columnExpr, string paramToken) =>
        IsPostgres(factory) ? $"{columnExpr} ILIKE {paramToken}" : $"{columnExpr} LIKE {paramToken} COLLATE NOCASE";

    /// <summary>Case-insensitive equality: SQLite — <c>= ... COLLATE NOCASE</c>, PG — <c>LOWER(...)=LOWER(...)</c>.</summary>
    public static string NoCaseEq(IDbConnectionFactory factory, string columnExpr, string paramToken) =>
        IsPostgres(factory) ? $"LOWER({columnExpr}) = LOWER({paramToken})" : $"{columnExpr} = {paramToken} COLLATE NOCASE";
}
