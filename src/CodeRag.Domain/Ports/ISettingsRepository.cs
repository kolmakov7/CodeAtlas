namespace CodeRag.Domain.Ports;

public interface ISettingsRepository
{
    Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task<Dictionary<string, string>> UpsertAsync(Dictionary<string, string> values, CancellationToken ct = default);
}
