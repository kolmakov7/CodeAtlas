using System.Diagnostics;
using CodeRag.Domain.Entities;
using CodeRag.Git;
using CodeRag.Git.Auth;
using CodeRag.Git.Helpers;
using CodeRag.Git.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer;

/// <summary>
/// CLI git реализация IGitService (git-proxy-auth.md §1, ADR-004).
/// Использует `git` из PATH, поддерживает http/https/socks5 proxy per-repo через env / -c http.proxy.
/// </summary>
public sealed class CliGitService : IGitService
{
    private readonly ILogger<CliGitService> _logger;
    private readonly GitOptions _options;
    private readonly ICredentialProtector _protector;

    public CliGitService(ILogger<CliGitService> logger, IOptions<GitOptions> options, ICredentialProtector protector)
    {
        _logger = logger;
        _options = options.Value;
        _protector = protector;
    }

    // New overload with Proxy/Credential (used by IndexingService after fix)
    public async Task FetchAsync(string repoPath, string branch, ProxyConfig? proxy, Credential? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("repoPath required", nameof(repoPath));
        if (!Directory.Exists(repoPath))
            throw new InvalidOperationException($"Local repo path does not exist: {repoPath}. Clone it first via IGitService/RepositoryService.");
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            throw new InvalidOperationException($"Not a git repository: {repoPath} (.git missing). Clone it first.");

        using var auth = GitAuthHelper.Build(credential, _protector, _options.SshKeysDir);
        var proxyUrl = GitProxyHelper.BuildProxyUrl(proxy, _protector);
        var proxyArgs = GitProxyHelper.BuildGitConfigArgs(proxyUrl);
        var proxyEnv = GitProxyHelper.BuildEnv(proxyUrl, _options.NoProxy);
        var env = MergeEnv(auth.Env, proxyEnv);

        var args = new List<string>();
        args.AddRange(proxyArgs);
        args.AddRange(auth.ConfigArgs);
        args.Add("fetch");
        args.Add("origin");
        if (!string.IsNullOrWhiteSpace(branch))
            args.Add(branch.Trim());

        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, repoPath, env, _logger, ct, _options.DefaultTimeoutSeconds).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git fetch failed in {repoPath}: {SecretMasker.Mask(result.StdErr)} {SecretMasker.Mask(result.StdOut)}");
        _logger.LogDebug("git fetch ok {Repo} branch={Branch}", repoPath, branch);
    }

    public async Task FetchAsync(string repoPath, string branch, string? proxyUrl, CancellationToken ct)
    {
        // Backward compat: proxyUrl string -> delegate to helper without credential
        // Build env/args via legacy helpers to keep old callers working
        if (!Directory.Exists(repoPath))
            throw new InvalidOperationException($"Local repo path does not exist: {repoPath}. Clone it first via IGitService/RepositoryService.");
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            throw new InvalidOperationException($"Not a git repository: {repoPath} (.git missing). Clone it first.");

        var env = BuildProxyEnv(proxyUrl);
        var args = string.IsNullOrWhiteSpace(branch) ? "fetch origin" : $"fetch origin {branch}";
        var proxyArgs = BuildProxyConfigArgs(proxyUrl);
        var fullArgs = string.IsNullOrEmpty(proxyArgs) ? args : $"{proxyArgs} {args}";
        var result = await RunGitAsync(repoPath, fullArgs, env, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git fetch failed in {repoPath}: {result.Stderr} {result.Stdout}");
        _logger.LogDebug("git fetch ok {Repo} branch={Branch}", repoPath, branch);
    }

    public async Task<string> GetHeadAsync(string repoPath, string branch, CancellationToken ct)
    {
        // Prefer remote HEAD: origin/branch
        var remoteRef = $"origin/{branch}";
        var r1 = await RunGitAsync(repoPath, $"rev-parse {remoteRef}", null, ct).ConfigureAwait(false);
        if (r1.ExitCode == 0)
            return r1.Stdout.Trim();

        // Fallback to local HEAD
        var r2 = await RunGitAsync(repoPath, "rev-parse HEAD", null, ct).ConfigureAwait(false);
        if (r2.ExitCode == 0)
            return r2.Stdout.Trim();

        throw new InvalidOperationException($"git rev-parse failed in {repoPath} branch {branch}: {r1.Stderr} {r2.Stderr}");
    }

    public async Task<GitDiff> DiffAsync(string repoPath, string? fromSha, string toSha, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toSha))
            throw new ArgumentException("toSha must not be empty", nameof(toSha));

        if (string.IsNullOrWhiteSpace(fromSha))
        {
            // Первая индексация — полный diff: все файлы — Added
            var files = await ListFilesAsync(repoPath, toSha, ct).ConfigureAwait(false);
            var changed = files.Select(p => new GitChangedFile(p, null, GitChangeKind.Added)).ToList();
            return new GitDiff(null, toSha, changed, IsFullDiff: true);
        }

        // Проверка предка для обнаружения rebase/reset/force-push (git-proxy-auth.md §1)
        var isAncestor = await IsAncestorAsync(repoPath, fromSha, toSha, ct).ConfigureAwait(false);
        if (!isAncestor)
        {
            _logger.LogWarning("fromSha {From} is not ancestor of {To} in {Repo} — full diff fallback", fromSha, toSha, repoPath);
            var toFiles = await ListFilesAsync(repoPath, toSha, ct).ConfigureAwait(false);
            var fromFiles = await ListFilesAsync(repoPath, fromSha, ct).ConfigureAwait(false);
            var toSet = new HashSet<string>(toFiles);
            var fromSet = new HashSet<string>(fromFiles);
            var changed = new List<GitChangedFile>();
            foreach (var f in toSet.Except(fromSet)) changed.Add(new GitChangedFile(f, null, GitChangeKind.Added));
            foreach (var f in fromSet.Except(toSet)) changed.Add(new GitChangedFile(f, null, GitChangeKind.Deleted));
            foreach (var f in toSet.Intersect(fromSet))
            {
                // Проверяем содержимое — если отличается, Modified
                var diff = await RunGitAsync(repoPath, $"diff --name-only {fromSha}..{toSha} -- {EscapePath(f)}", null, ct).ConfigureAwait(false);
                if (diff.Stdout.Trim().Length > 0)
                    changed.Add(new GitChangedFile(f, null, GitChangeKind.Modified));
            }
            // Дополнительно: git diff --name-status -M для переименований
            var nameStatus = await RunGitAsync(repoPath, $"diff --name-status -M {fromSha}..{toSha}", null, ct).ConfigureAwait(false);
            if (nameStatus.ExitCode == 0)
            {
                var parsed = ParseNameStatus(nameStatus.Stdout);
                // Переопределяем changed если name-status дал переименования
                var renameMap = parsed.Where(p => p.Kind == GitChangeKind.Renamed).ToList();
                foreach (var r in renameMap)
                {
                    changed.RemoveAll(c => c.Path == r.Path || c.Path == r.OldPath);
                    changed.Add(r);
                }
            }
            return new GitDiff(fromSha, toSha, changed, IsFullDiff: true);
        }

        // Нормальный инкрементальный diff
        var result = await RunGitAsync(repoPath, $"diff --name-status -M {fromSha}..{toSha}", null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("git diff failed {From}..{To} in {Repo}: {Err} — fallback to full", fromSha, toSha, repoPath, result.Stderr);
            var files = await ListFilesAsync(repoPath, toSha, ct).ConfigureAwait(false);
            return new GitDiff(fromSha, toSha, files.Select(p => new GitChangedFile(p, null, GitChangeKind.Added)).ToList(), true);
        }

        var changedFiles = ParseNameStatus(result.Stdout);
        return new GitDiff(fromSha, toSha, changedFiles, IsFullDiff: false);
    }

    public async Task<bool> IsAncestorAsync(string repoPath, string ancestor, string descendant, CancellationToken ct)
    {
        var r = await RunGitAsync(repoPath, $"merge-base --is-ancestor {ancestor} {descendant}", null, ct).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct)
    {
        var r = await RunGitAsync(repoPath, $"ls-tree -r --name-only {commitSha}", null, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"git ls-tree failed for {commitSha} in {repoPath}: {r.Stderr}");
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string EscapePath(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private static IReadOnlyList<GitChangedFile> ParseNameStatus(string stdout)
    {
        var list = new List<GitChangedFile>();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // Format: "M\tpath" or "A\tpath" or "D\tpath" or "R100\told\tnew"
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var status = parts[0].Trim();
            var kind = status[0] switch
            {
                'A' => GitChangeKind.Added,
                'M' => GitChangeKind.Modified,
                'D' => GitChangeKind.Deleted,
                'R' => GitChangeKind.Renamed,
                'C' => GitChangeKind.Copied,
                'U' => GitChangeKind.Unmerged,
                _ => GitChangeKind.Modified
            };
            if (kind == GitChangeKind.Renamed && parts.Length >= 3)
                list.Add(new GitChangedFile(parts[2].Trim(), parts[1].Trim(), kind));
            else
                list.Add(new GitChangedFile(parts[1].Trim(), null, kind));
        }
        return list;
    }

    private static Dictionary<string, string?> BuildProxyEnv(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return new Dictionary<string, string?>();
        // Поддержка http/https/socks5h (git-proxy-auth.md §2)
        return new Dictionary<string, string?>
        {
            ["http_proxy"] = proxyUrl,
            ["https_proxy"] = proxyUrl,
            ["HTTP_PROXY"] = proxyUrl,
            ["HTTPS_PROXY"] = proxyUrl,
        };
    }

    private static string BuildProxyConfigArgs(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return string.Empty;
        // Для SOCKS5h git config http.proxy — достаточно
        var escaped = proxyUrl.Replace("\"", "\\\"");
        return $"-c http.proxy=\"{escaped}\" -c https.proxy=\"{escaped}\"";
    }

    private static Dictionary<string, string> MergeEnv(IDictionary<string, string> a, IDictionary<string, string> b)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in a) res[kv.Key] = kv.Value;
        foreach (var kv in b) res[kv.Key] = kv.Value;
        res["GIT_TERMINAL_PROMPT"] = "0";
        return res;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(string workDir, string args, Dictionary<string, string?>? env, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workDir))
            throw new ArgumentException("workDir required", nameof(workDir));
        if (!Directory.Exists(workDir))
            throw new InvalidOperationException($"Local repo path does not exist: {workDir}. Clone it first via RepositoryService.");
        if (!Directory.Exists(Path.Combine(workDir, ".git")) && !args.Contains("ls-remote"))
        {
            // For local ops (rev-parse/ls-tree/diff) require .git; ls-remote is remote-only
            var needsGit = args.StartsWith("rev-parse") || args.StartsWith("ls-tree") || args.StartsWith("diff") || args.StartsWith("merge-base") || args.StartsWith("fetch");
            if (needsGit)
                throw new InvalidOperationException($"Not a git repository: {workDir} (.git missing). Clone it first.");
        }

        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
            foreach (var kv in env)
                if (kv.Value is not null) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.Message.Contains("No such file or directory"))
        {
            throw new InvalidOperationException($"Local repo path does not exist: {workDir}. Clone it first.", ex);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
