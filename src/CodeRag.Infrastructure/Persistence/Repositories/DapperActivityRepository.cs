using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence.Repositories;

public sealed class DapperActivityRepository : IActivityRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DapperActivityRepository> _logger;
    public DapperActivityRepository(IDbConnectionFactory factory, ILogger<DapperActivityRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ActivityDto>> ListAsync(int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await conn.QueryAsync<ActivityRow>(
                "SELECT id, at, actor, action, repository_id as repositoryId, detail FROM activity_log ORDER BY at DESC LIMIT @limit;",
                new { limit }).ConfigureAwait(false);
            return rows.Select(r => new ActivityDto(r.id, r.at, r.actor, r.action, r.repositoryId, r.detail)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query activity_log, returning empty");
            return Array.Empty<ActivityDto>();
        }
    }

    public async Task AddAsync(ActivityLog entry, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT INTO activity_log(id, at, actor, action, repository_id, detail) VALUES (@id,@at,@actor,@action,@repo,@detail);",
            new { id = entry.Id.ToString(), at = entry.At.ToString("o"), actor = entry.Actor, action = entry.Action, repo = entry.RepositoryId?.ToString(), detail = entry.Detail }).ConfigureAwait(false);
    }

    private sealed class ActivityRow
    {
        public string id { get; set; } = "";
        public string at { get; set; } = "";
        public string actor { get; set; } = "";
        public string action { get; set; } = "";
        public string? repositoryId { get; set; }
        public string? detail { get; set; }
    }
}
