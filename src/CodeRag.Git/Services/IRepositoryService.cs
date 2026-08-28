using CodeRag.Domain.Entities;

namespace CodeRag.Git.Services;

/// <summary>Жизненный цикл репозиториев: Add/View/Edit/Sync/Re-index/Pause/Resume/Delete.</summary>
public interface IRepositoryService
{
    Task<Repository> AddAsync(string name, string gitUrl, string branch, string localPath, Guid? proxyId = null, Guid? credentialId = null, int? syncIntervalSeconds = null, CancellationToken ct = default);
    Task<Repository?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default);
    Task<Repository> UpdateAsync(Guid id, string? name = null, string? gitUrl = null, string? branch = null, string? localPath = null, Guid? proxyId = null, Guid? credentialId = null, int? syncIntervalSeconds = null, CancellationToken ct = default);
    Task SyncAsync(Guid id, CancellationToken ct = default);
    Task ReindexAsync(Guid id, CancellationToken ct = default);
    Task PauseAsync(Guid id, CancellationToken ct = default);
    Task ResumeAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, bool deleteLocalFiles = false, CancellationToken ct = default);
}
