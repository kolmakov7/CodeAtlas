using CodeRag.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Api.Services;

public sealed class SettingsService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(SqliteConnectionFactory factory, ILogger<SettingsService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Row>("SELECT key, value FROM settings;").ConfigureAwait(false);
        return rows.ToDictionary(r => r.key, r => r.value);
    }

    public async Task<Dictionary<string, string>> UpsertAsync(Dictionary<string, string> values, CancellationToken ct = default)
    {
        if (values is null || values.Count == 0) return await GetAllAsync(ct).ConfigureAwait(false);

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var kv in values)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                // Do not log secret values
                _logger.LogInformation("Updating setting '{Key}'", kv.Key);
                await conn.ExecuteAsync(
                    "INSERT INTO settings(key, value, updated_at) VALUES (@k,@v,@at) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at;",
                    new { k = kv.Key.Trim(), v = kv.Value, at = now }, transaction: tx).ConfigureAwait(false);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return await GetAllAsync(ct).ConfigureAwait(false);
    }

    private sealed class Row { public string key { get; set; } = ""; public string value { get; set; } = ""; }
}
