namespace CodeRag.Domain.Ports;

public sealed record HealthProbeResult(string Status, string? Message, double? LatencyMs);

public interface IDbHealthProbe
{
    Task<HealthProbeResult> CheckAsync(CancellationToken ct = default);
}

public interface IStorageProbe
{
    HealthProbeResult Check();
}
