using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

/// <summary>Dapper persistence for <c>repository_links</c>.</summary>
public sealed class RepositoryLinkStore : IRepositoryLinkStore
{
    private readonly IDbConnectionFactory _factory;

    public RepositoryLinkStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(RepositoryLink link, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO repository_links(id, source_repository_id, target_repository_id, link_type, confidence, note, created_at)
VALUES (@id, @source, @target, @type, @confidence, @note, @created)
ON CONFLICT(source_repository_id, target_repository_id, link_type) DO UPDATE SET
  confidence = excluded.confidence,
  note = excluded.note;";
        await conn.ExecuteAsync(sql, new
        {
            id = link.Id.ToString(),
            source = link.SourceRepositoryId.ToString(),
            target = link.TargetRepositoryId.ToString(),
            type = link.LinkType,
            confidence = link.Confidence,
            note = link.Note,
            created = link.CreatedAt.ToString("o")
        }).ConfigureAwait(false);
    }

    public async Task<RepositoryLink?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<LinkRow>(
            "SELECT * FROM repository_links WHERE id = @id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<RepositoryLink>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<LinkRow>(
            "SELECT * FROM repository_links WHERE source_repository_id = @r OR target_repository_id = @r ORDER BY created_at DESC;",
            new { r = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<RepositoryLink>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<LinkRow>("SELECT * FROM repository_links ORDER BY created_at DESC;").ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM repository_links WHERE id = @id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    private static RepositoryLink Map(LinkRow r) => RepositoryLink.Rehydrate(
        Guid.Parse(r.id), Guid.Parse(r.source_repository_id), Guid.Parse(r.target_repository_id),
        r.link_type, r.confidence, r.note,
        DateTimeOffset.TryParse(r.created_at, out var at) ? at : DateTimeOffset.UtcNow);

    private sealed class LinkRow
    {
        public string id { get; set; } = "";
        public string source_repository_id { get; set; } = "";
        public string target_repository_id { get; set; } = "";
        public string link_type { get; set; } = "";
        public double? confidence { get; set; }
        public string? note { get; set; }
        public string created_at { get; set; } = "";
    }
}
