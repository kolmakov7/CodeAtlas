using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Git.Stores;

public sealed class ProxyStore : IProxyStore
{
    private readonly SqliteConnectionFactory _factory;
    public ProxyStore(SqliteConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(ProxyConfig proxy, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO proxies(id, name, scheme, host, port, username, password_encrypted)
VALUES (@id,@name,@scheme,@host,@port,@user,@pass)
ON CONFLICT(id) DO UPDATE SET name=excluded.name, scheme=excluded.scheme, host=excluded.host, port=excluded.port, username=excluded.username, password_encrypted=excluded.password_encrypted;";
        await conn.ExecuteAsync(sql, new
        {
            id = proxy.Id.ToString(),
            name = proxy.Name,
            scheme = proxy.Scheme.ToString(),
            host = proxy.Host,
            port = proxy.Port,
            user = proxy.Username,
            pass = proxy.PasswordEncrypted
        }).ConfigureAwait(false);
    }

    public async Task<ProxyConfig?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<ProxyRow>("SELECT * FROM proxies WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<ProxyConfig>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ProxyRow>("SELECT * FROM proxies ORDER BY name;").ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM proxies WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    private static ProxyConfig Map(ProxyRow r)
    {
        var scheme = Enum.TryParse<ProxyScheme>(r.scheme, out var s) ? s : ProxyScheme.Http;
        return ProxyConfig.Create(Guid.Parse(r.id), r.name, scheme, r.host, r.port, r.username, r.password_encrypted);
    }

    private sealed class ProxyRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string scheme { get; set; } = "";
        public string host { get; set; } = "";
        public int port { get; set; }
        public string? username { get; set; }
        public string? password_encrypted { get; set; }
    }
}
