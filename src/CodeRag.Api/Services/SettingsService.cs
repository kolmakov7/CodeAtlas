using CodeRag.Domain.Ports;

namespace CodeRag.Api.Services;

public sealed class SettingsService
{
    private readonly ISettingsRepository _repo;
    public SettingsService(ISettingsRepository repo) => _repo = repo;

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) => _repo.GetAllAsync(ct);
    public Task<Dictionary<string, string>> UpsertAsync(Dictionary<string, string> values, CancellationToken ct = default) => _repo.UpsertAsync(values, ct);
}
