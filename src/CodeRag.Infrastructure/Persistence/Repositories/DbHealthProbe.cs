using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence.Repositories;

public sealed class DbHealthProbe : IDbHealthProbe
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DbHealthProbe> _logger;
    public DbHealthProbe(IDbConnectionFactory factory, ILogger<DbHealthProbe> logger)
    {
        _factory = factory;
        _logger = logger;
    }
    public async Task<HealthProbeResult> CheckAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
            await conn.ExecuteScalarAsync("SELECT 1;").ConfigureAwait(false);
            sw.Stop();
            return new HealthProbeResult("Healthy", null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Health DB check failed");
            return new HealthProbeResult("Unhealthy", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }
}

public sealed class StorageProbe : IStorageProbe
{
    private readonly IDbConnectionFactory _factory;
    public StorageProbe(IDbConnectionFactory factory) => _factory = factory;
    public HealthProbeResult Check()
    {
        try
        {
            var dbPath = _factory.DbPath;
            var dir = Path.GetDirectoryName(dbPath) ?? "/data";
            if (!Directory.Exists(dir)) return new HealthProbeResult("Unhealthy", $"data dir missing: {dir}", null);
            var testFile = Path.Combine(dir, ".healthcheck");
            try { File.WriteAllText(testFile, "ok"); File.Delete(testFile); }
            catch (Exception ex) { return new HealthProbeResult("Degraded", $"storage not writable: {ex.Message}", null); }
            return new HealthProbeResult("Healthy", null, null);
        }
        catch (Exception ex) { return new HealthProbeResult("Unhealthy", ex.Message, null); }
    }
}
