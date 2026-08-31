namespace CodeRag.Application.Ports;

public sealed record HealthComponent(string Status, string? Message, double? LatencyMs);
public sealed record HealthDetails(
    string Status,
    HealthComponent Database,
    HealthComponent Vector,
    HealthComponent Embedding,
    HealthComponent Indexer,
    HealthComponent Storage,
    IReadOnlyList<string> Warnings);

public interface IHealthService
{
    Task<HealthDetails> CheckDetailedAsync(CancellationToken ct = default);
    Task<(bool Healthy, string? Message)> CheckReadyAsync(CancellationToken ct = default);
}
