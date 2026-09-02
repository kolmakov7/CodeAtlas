using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Git.Stores;

public sealed class CredentialStore : ICredentialStore
{
    private readonly IDbConnectionFactory _factory;
    public CredentialStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Credential credential, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO credentials(id, kind, username, secret_encrypted, ssh_key_path)
VALUES (@id,@kind,@user,@secret,@path)
ON CONFLICT(id) DO UPDATE SET kind=excluded.kind, username=excluded.username, secret_encrypted=excluded.secret_encrypted, ssh_key_path=excluded.ssh_key_path;";
        await conn.ExecuteAsync(sql, new
        {
            id = credential.Id.ToString(),
            kind = credential.Kind.ToString(),
            user = credential.Username,
            secret = credential.SecretEncrypted,
            path = credential.SshKeyPath
        }).ConfigureAwait(false);
    }

    public async Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<CredRow>("SELECT * FROM credentials WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<CredRow>("SELECT * FROM credentials ORDER BY kind;").ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM credentials WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    private static Credential Map(CredRow r)
    {
        var kind = Enum.TryParse<CredentialKind>(r.kind, out var k) ? k : CredentialKind.HttpsToken;
        return Credential.Create(Guid.Parse(r.id), kind, r.username, r.secret_encrypted, r.ssh_key_path);
    }

    private sealed class CredRow
    {
        public string id { get; set; } = "";
        public string kind { get; set; } = "";
        public string? username { get; set; }
        public string? secret_encrypted { get; set; }
        public string? ssh_key_path { get; set; }
    }
}
