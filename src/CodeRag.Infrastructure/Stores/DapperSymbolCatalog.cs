using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class DapperSymbolCatalog : ISymbolCatalog
{
    private readonly IDbConnectionFactory _factory;
    public DapperSymbolCatalog(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var sql = language is null
            ? "SELECT id, fqn, name FROM symbols WHERE repository_id=@repo;"
            : "SELECT id, fqn, name FROM symbols WHERE repository_id=@repo AND language=@lang;";
        object param = language is null
            ? new { repo = repositoryId.ToString() }
            : new { repo = repositoryId.ToString(), lang = language };
        var rows = await conn.QueryAsync<Row>(sql, param).ConfigureAwait(false);
        return rows.Select(r => new CatalogSymbol(Guid.Parse(r.id), r.fqn, r.name)).ToList();
    }

    private sealed class Row
    {
        public string id { get; set; } = "";
        public string? fqn { get; set; }
        public string name { get; set; } = "";
    }
}
