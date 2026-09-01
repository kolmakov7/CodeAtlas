using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git.Helpers;
using CodeRag.Git.Stores;
using CodeRag.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Git.Services;

public sealed class RepositoryService : IRepositoryService
{
    private readonly IRepositoryStore _repoStore;
    private readonly IProxyStore _proxyStore;
    private readonly ICredentialStore _credStore;
    private readonly IRepositoryGroupStore _groupStore;
    private readonly IGitService _git;
    private readonly SqliteConnectionFactory _factory;
    private readonly GitOptions _opts;
    private readonly ILogger<RepositoryService> _logger;

    public RepositoryService(
        IRepositoryStore repoStore,
        IProxyStore proxyStore,
        ICredentialStore credStore,
        IRepositoryGroupStore groupStore,
        IGitService git,
        SqliteConnectionFactory factory,
        IOptions<GitOptions> opts,
        ILogger<RepositoryService> logger)
    {
        _repoStore = repoStore;
        _proxyStore = proxyStore;
        _credStore = credStore;
        _groupStore = groupStore;
        _git = git;
        _factory = factory;
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<Repository> AddAsync(string name, string gitUrl, string branch, string localPath, Guid? groupId = null, int? syncIntervalSeconds = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var normalized = PathValidator.ValidateAndNormalize(localPath, _opts.ReposRoot);

        // Resolve group — default to Default group if not specified
        Guid? effectiveGroupId = groupId;
        ProxyConfig? proxy = null;
        Credential? cred = null;
        if (effectiveGroupId.HasValue)
        {
            var group = await _groupStore.GetByIdAsync(effectiveGroupId.Value, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"RepositoryGroup {effectiveGroupId} not found.");
            if (group.ProxyId.HasValue)
                proxy = await _proxyStore.GetByIdAsync(group.ProxyId.Value, ct).ConfigureAwait(false);
            if (group.CredentialId.HasValue)
                cred = await _credStore.GetByIdAsync(group.CredentialId.Value, ct).ConfigureAwait(false);
        }
        else
        {
            var def = await _groupStore.GetByNameAsync("Default", ct).ConfigureAwait(false);
            if (def is not null) effectiveGroupId = def.Id;
        }

        var repo = Repository.Create(id, name, gitUrl, branch, normalized, RepositoryStatus.Active, true, effectiveGroupId, syncIntervalSeconds);
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        // Validate remote reachable via ls-remote перед клонированием (быстрая проверка)
        try
        {
            await _git.LsRemoteAsync(gitUrl, branch, proxy, cred, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var masked = SecretMasker.Mask(ex.Message);
            repo.MarkError(masked);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            _logger.LogWarning(ex, "Add repo {Name} ls-remote failed: {Err}", name, masked);
            // Не бросаем — оставляем в Error статусе, UI покажет LastError
            return repo;
        }

        // Attempt clone
try
            {
                repo.MarkSyncing();
                await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

                await _git.CloneAsync(gitUrl, normalized, branch, proxy, cred, ct).ConfigureAwait(false);

                var head = await _git.GetHeadAsync(normalized, branch, ct).ConfigureAwait(false);
                repo.UpdateCurrentCommit(head);
                repo.MarkActive();
                repo.ClearError();
                await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

                _logger.LogInformation("Repository {Name} cloned at {Path} head {Sha}", name, normalized, head);

                // Enqueue initial index job
                // Note: This requires IJobRunner which is in CodeRag.Indexer - we'll handle via the background service polling instead
            }
        catch (Exception ex)
        {
            var masked = SecretMasker.Mask(ex.Message);
            repo.MarkError(masked);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Clone failed for {Name}: {Err}", name, masked);
        }

        return repo;
    }

    public Task<Repository?> GetAsync(Guid id, CancellationToken ct = default) => _repoStore.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default) => _repoStore.ListAsync(ct);

    public async Task<Repository> UpdateAsync(Guid id, string? name = null, string? gitUrl = null, string? branch = null, string? localPath = null, Guid? groupId = null, int? syncIntervalSeconds = null, CancellationToken ct = default)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Repository {id} not found.");
        if (repo.Status == RepositoryStatus.Deleting) throw new InvalidOperationException("Repository is being deleted.");

        bool branchChanged = false;
        string oldBranch = repo.Branch;

        if (name is not null) repo.Rename(name);
        if (gitUrl is not null) repo.ChangeGitUrl(gitUrl);
        if (branch is not null && !string.Equals(branch.Trim(), repo.Branch, StringComparison.Ordinal))
        {
            repo.ChangeBranch(branch);
            branchChanged = true;
        }
        if (localPath is not null)
        {
            var n = PathValidator.ValidateAndNormalize(localPath, _opts.ReposRoot);
            repo.ChangeLocalPath(n);
        }
        if (groupId is not null)
        {
            var gid = groupId == Guid.Empty ? null : groupId;
            if (gid.HasValue)
            {
                var g = await _groupStore.GetByIdAsync(gid.Value, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"RepositoryGroup {gid} not found.");
            }
            repo.SetGroup(gid);
        }
        if (syncIntervalSeconds.HasValue) repo.SetSyncInterval(syncIntervalSeconds);
        else if (syncIntervalSeconds is null && groupId is null && name is null && gitUrl is null && branch is null && localPath is null)
        {
            // no-op
        }

        repo.ClearError();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        if (branchChanged)
        {
            _logger.LogInformation("Branch changed for {Name}: {Old} -> {New}, triggering fetch", repo.Name, oldBranch, repo.Branch);
            await SyncAsync(id, ct).ConfigureAwait(false);
            // Reload after sync
            repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false) ?? repo;
        }

        return repo;
    }

    public async Task<(ProxyConfig? Proxy, Credential? Credential)> GetEffectiveCredentialsAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var repo = await _repoStore.GetByIdAsync(repositoryId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {repositoryId} not found.");
        if (!repo.GroupId.HasValue) return (null, null);
        var group = await _groupStore.GetByIdAsync(repo.GroupId.Value, ct).ConfigureAwait(false);
        if (group is null) return (null, null);
        ProxyConfig? proxy = group.ProxyId.HasValue ? await _proxyStore.GetByIdAsync(group.ProxyId.Value, ct).ConfigureAwait(false) : null;
        Credential? cred = group.CredentialId.HasValue ? await _credStore.GetByIdAsync(group.CredentialId.Value, ct).ConfigureAwait(false) : null;
        return (proxy, cred);
    }

    public async Task SyncAsync(Guid id, CancellationToken ct = default)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Repository {id} not found.");
        if (repo.Status == RepositoryStatus.Paused)
        {
            _logger.LogInformation("Sync skipped for paused repository {Name}", repo.Name);
            return;
        }
        if (repo.Status == RepositoryStatus.Deleting) return;

        var (proxy, cred) = await GetEffectiveCredentialsAsync(id, ct).ConfigureAwait(false);

        try
        {
            repo.MarkSyncing();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

            var localPath = repo.LocalPath.Value;
            // If not cloned yet, clone
            if (!Directory.Exists(Path.Combine(localPath, ".git")))
            {
                await _git.CloneAsync(repo.GitUrl.Value, localPath, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            else
            {
                await _git.FetchAsync(localPath, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }

            var newHead = await _git.GetHeadAsync(localPath, repo.Branch, ct).ConfigureAwait(false);
            repo.UpdateCurrentCommit(newHead);
            repo.ClearError();
            // Status stays Syncing until indexing done; caller (Indexer) will move to Indexing then Active.
            // For MVP without indexer, we set Active directly if no indexing required.
            // Check if IndexedCommit == CurrentCommit -> Active else keep Syncing? For now set Active then let polling handle diff.
            if (repo.IndexedCommit is null || repo.IndexedCommit == newHead)
            {
                repo.MarkActive();
            }
            else
            {
                // There are changes to index; we could enqueue job. For now mark Active and log diff.
                _logger.LogInformation("Repository {Name} new head {Sha} diff from {From}", repo.Name, newHead, repo.IndexedCommit);
                // Attempt diff for diagnostic (may fail if ancestors not related -> handled by IsAncestor)
                try
                {
                    bool isAncestor = await _git.IsAncestorAsync(localPath, repo.IndexedCommit!, newHead, ct).ConfigureAwait(false);
                    if (!isAncestor)
                        _logger.LogWarning("Rebase/force-push detected for {Name}: {From} not ancestor of {To}", repo.Name, repo.IndexedCommit, newHead);
                    var diff = await _git.DiffAsync(localPath, repo.IndexedCommit!, newHead, ct).ConfigureAwait(false);
                    _logger.LogInformation("Diff for {Name}: {Count} entries", repo.Name, diff.Entries.Count);
                }
                catch (Exception diffEx)
                {
                    _logger.LogWarning(diffEx, "Diff failed for {Name}", repo.Name);
                }
                repo.MarkActive();
            }

            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var masked = SecretMasker.Mask(ex.Message);
            repo.MarkError(masked);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Sync failed for {Name}: {Err}", repo.Name, masked);
            throw new InvalidOperationException(masked, ex);
        }
    }

    public async Task ReindexAsync(Guid id, CancellationToken ct = default)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Repository {id} not found.");
        if (repo.Status == RepositoryStatus.Deleting) return;

        var (proxy, cred) = await GetEffectiveCredentialsAsync(id, ct).ConfigureAwait(false);

        try
        {
            repo.MarkIndexing();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

            var localPath = repo.LocalPath.Value;
            // Ensure fetch first
            await _git.FetchAsync(localPath, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            var head = await _git.GetHeadAsync(localPath, repo.Branch, ct).ConfigureAwait(false);
            repo.UpdateCurrentCommit(head);

            // Пустой diff (re-index): all files via ls-tree
            var files = await _git.LsTreeAsync(localPath, head, ct).ConfigureAwait(false);
            _logger.LogInformation("Reindex for {Name}: {Count} files at {Sha}", repo.Name, files.Count, head);

            // In real indexer, here would enqueue IndexingJob with full diff. For Phase 2 we just simulate completion: IndexedCommit = CurrentCommit
            repo.MarkIndexSucceeded(head);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var masked = SecretMasker.Mask(ex.Message);
            repo.MarkError(masked);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Reindex failed for {Name}: {Err}", repo.Name, masked);
            throw;
        }
    }

    public async Task PauseAsync(Guid id, CancellationToken ct = default)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Repository {id} not found.");
        repo.MarkPaused();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        _logger.LogInformation("Repository {Name} paused", repo.Name);
    }

    public async Task ResumeAsync(Guid id, CancellationToken ct = default)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Repository {id} not found.");
        if (repo.Status != RepositoryStatus.Paused && repo.Status != RepositoryStatus.Error)
        {
            _logger.LogInformation("Resume skipped for {Name} status {Status}", repo.Name, repo.Status);
            return;
        }
        repo.ClearError();
        repo.MarkActive();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        _logger.LogInformation("Repository {Name} resumed", repo.Name);
    }

    public async Task DeleteAsync(Guid id, bool deleteLocalFiles = false, CancellationToken ct = default)
    {
        // Идемпотентно: повторный DELETE на несуществующий id → success
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null)
        {
            _logger.LogInformation("Delete idempotent: repository {Id} not found", id);
            return;
        }

        var localPath = repo.LocalPath.Value;
        var normalized = PathValidator.ValidateAndNormalize(localPath, _opts.ReposRoot);

        // Mark deleting
        try
        {
            repo.MarkDeleting();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        }
        catch { }

        // Каскад DELETE vector_entries+chunks_fts+vec_chunks + repositories ON DELETE CASCADE
        // RepositoryStore.DeleteCascadeAsync уже делает это в транзакции, но также используем явную очистку для безопасности.
        try
        {
            await PerformCascadeDeleteAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cascade delete failed for {Name}", repo.Name);
            throw;
        }

        if (deleteLocalFiles)
        {
            try
            {
                if (Directory.Exists(normalized))
                {
                    Directory.Delete(normalized, recursive: true);
                    _logger.LogInformation("Deleted local path {Path} for repo {Name}", normalized, repo.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete local path {Path}", normalized);
                // Не фейлить удаление из БД из-за FS ошибки
            }
        }
        else
        {
            _logger.LogInformation("Repository {Name} deleted from DB, local checkout preserved at {Path}", repo.Name, normalized);
        }
    }

    private async Task PerformCascadeDeleteAsync(Guid repositoryId, CancellationToken ct)
    {
        // Try via IRepositoryStore.DeleteCascadeAsync if available
        if (_repoStore is CodeRag.Infrastructure.Stores.RepositoryStore concrete)
        {
            await concrete.DeleteCascadeAsync(repositoryId, ct).ConfigureAwait(false);
            return;
        }

        // Fallback generic SQL via factory
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            var rid = repositoryId.ToString();
            await conn.ExecuteAsync("DELETE FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id = @rid);", new { rid }, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM indexing_errors WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM activity_log WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            foreach (var table in new[] { "vec_chunks_code", "vec_chunks_text", "vec_chunks" })
            {
                try { await conn.ExecuteAsync($"DELETE FROM {table} WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false); } catch { }
            }
            await conn.ExecuteAsync("DELETE FROM repositories WHERE id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        try
        {
            using var vacConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
            await vacConn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
            await vacConn.ExecuteAsync("VACUUM;").ConfigureAwait(false);
        }
        catch { }
    }
}
