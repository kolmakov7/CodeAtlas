using CodeRag.Domain.Entities;
using CodeRag.Git.Auth;
using CodeRag.Git.Helpers;
using CodeRag.Git.Models;
using CodeRag.Git.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Git;

public sealed class GitService : IGitService
{
    private readonly GitOptions _options;
    private readonly ICredentialProtector _protector;
    private readonly ILogger<GitService> _logger;

    public GitService(IOptions<GitOptions> options, ICredentialProtector protector, ILogger<GitService> logger)
    {
        _options = options.Value;
        _protector = protector;
        _logger = logger;
    }

    public async Task CloneAsync(string remoteUrl, string localPath, string branch, ProxyConfig? proxy = null, Credential? credential = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) throw new ArgumentException("remoteUrl required", nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(branch)) branch = "main";

        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);

        // If directory already exists and is a git repo, skip clone (idempotent)
        if (Directory.Exists(Path.Combine(normalized, ".git")))
        {
            _logger.LogInformation("Clone skipped, repo already exists at {Path}", normalized);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(normalized) ?? normalized);
        // Ensure parent exists; remove empty stale dir if exists but not git
        if (Directory.Exists(normalized))
        {
            if (!Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                // empty, ok to clone into it (git clone expects non-existing or empty)
            }
            else
            {
                // not empty and not git => error
                throw new InvalidOperationException($"Local path {normalized} already exists and is not a git repository.");
            }
        }

        using var auth = GitAuthHelper.Build(credential, _protector, _options.SshKeysDir);
        var proxyUrl = GitProxyHelper.BuildProxyUrl(proxy, _protector);
        var proxyArgs = GitProxyHelper.BuildGitConfigArgs(proxyUrl);
        var proxyEnv = GitProxyHelper.BuildEnv(proxyUrl, _options.NoProxy);

        var args = new List<string>();
        args.AddRange(proxyArgs);
        args.AddRange(auth.ConfigArgs);
        args.Add("clone");
        args.Add("--branch");
        args.Add(branch);
        // Avoid credential leak in URL: remoteUrl already without token
        args.Add(remoteUrl);
        args.Add(normalized);

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, _options.DefaultTimeoutSeconds).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw CreateGitException("clone", result, proxyUrl);
    }

    public async Task FetchAsync(string localPath, string branch, ProxyConfig? proxy = null, Credential? credential = null, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        if (!Directory.Exists(Path.Combine(normalized, ".git")))
            throw new InvalidOperationException($"Not a git repository: {normalized}");

        if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("branch required", nameof(branch));

        using var auth = GitAuthHelper.Build(credential, _protector, _options.SshKeysDir);
        var proxyUrl = GitProxyHelper.BuildProxyUrl(proxy, _protector);
        var proxyArgs = GitProxyHelper.BuildGitConfigArgs(proxyUrl);
        var proxyEnv = GitProxyHelper.BuildEnv(proxyUrl, _options.NoProxy);

        var args = new List<string>();
        args.AddRange(proxyArgs);
        args.AddRange(auth.ConfigArgs);
        args.Add("fetch");
        args.Add("origin");
        args.Add(branch);
        // optional: --prune ?

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, env, _logger, ct, _options.DefaultTimeoutSeconds).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw CreateGitException("fetch", result, proxyUrl);
    }

    public async Task<GitDiff> DiffAsync(string localPath, string fromSha, string toSha, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        if (string.IsNullOrWhiteSpace(fromSha) || string.IsNullOrWhiteSpace(toSha))
            throw new ArgumentException("fromSha and toSha required");

        // git diff --name-status -M from..to
        var args = new List<string> { "diff", "--name-status", "-M", $"{fromSha}..{toSha}" };
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, null, _logger, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw CreateGitException("diff", result, null);

        var entries = ParseDiff(result.StdOut);
        return new GitDiff(fromSha.Trim(), toSha.Trim(), entries);
    }

    public async Task<string> GetHeadAsync(string localPath, string branch, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        // Try rev-parse origin/branch
        var args = new List<string> { "rev-parse", $"origin/{branch}" };
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, null, _logger, ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            var sha = result.StdOut.Trim();
            if (IsValidSha(sha)) return sha;
        }

        // Fallback: rev-parse HEAD
        var headArgs = new List<string> { "rev-parse", "HEAD" };
        var headResult = await GitProcessRunner.RunAsync(_options.GitExecutable, headArgs, normalized, null, _logger, ct).ConfigureAwait(false);
        if (headResult.ExitCode == 0)
        {
            var sha = headResult.StdOut.Trim();
            if (IsValidSha(sha)) return sha;
        }

        throw CreateGitException("rev-parse", result, null);
    }

    public async Task<bool> IsAncestorAsync(string localPath, string ancestorSha, string descendantSha, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        if (!IsValidSha(ancestorSha) || !IsValidSha(descendantSha))
            throw new ArgumentException("Invalid SHA");

        var args = new List<string> { "merge-base", "--is-ancestor", ancestorSha.Trim(), descendantSha.Trim() };
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, null, _logger, ct).ConfigureAwait(false);
        // exit 0 = true, 1 = false, other = error
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1) return false;
        throw CreateGitException("merge-base --is-ancestor", result, null);
    }

    public async Task<IReadOnlyList<string>> LsTreeAsync(string localPath, string commitSha, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        if (!IsValidSha(commitSha)) throw new ArgumentException($"Invalid commit SHA: {commitSha}");

        var args = new List<string> { "ls-tree", "-r", "--name-only", commitSha.Trim() };
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, null, _logger, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw CreateGitException("ls-tree", result, null);

        var files = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return files;
    }

    public async Task<string?> LsRemoteAsync(string remoteUrl, string? branch = null, ProxyConfig? proxy = null, Credential? credential = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) throw new ArgumentException("remoteUrl required", nameof(remoteUrl));

        using var auth = GitAuthHelper.Build(credential, _protector, _options.SshKeysDir);
        var proxyUrl = GitProxyHelper.BuildProxyUrl(proxy, _protector);
        var proxyArgs = GitProxyHelper.BuildGitConfigArgs(proxyUrl);
        var proxyEnv = GitProxyHelper.BuildEnv(proxyUrl, _options.NoProxy);

        var args = new List<string>();
        args.AddRange(proxyArgs);
        args.AddRange(auth.ConfigArgs);
        args.Add("ls-remote");
        args.Add(remoteUrl);
        if (!string.IsNullOrWhiteSpace(branch))
            args.Add(branch!);

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, _options.DefaultTimeoutSeconds).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw CreateGitException("ls-remote", result, proxyUrl);

        // Output like: <sha>\t<ref>
        var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var first = lines[0].Trim();
        var tab = first.IndexOf('\t');
        if (tab > 0)
            return first.Substring(0, tab).Trim();
        var sp = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return sp.Length > 0 ? sp[0].Trim() : null;
    }

    public async Task<IReadOnlyList<GitRemoteRef>> LsRemoteAllAsync(string remoteUrl, ProxyConfig? proxy = null, Credential? credential = null, CancellationToken ct = default)
    {
        using var auth = GitAuthHelper.Build(credential, _protector, _options.SshKeysDir);
        var proxyUrl = GitProxyHelper.BuildProxyUrl(proxy, _protector);
        var proxyArgs = GitProxyHelper.BuildGitConfigArgs(proxyUrl);
        var proxyEnv = GitProxyHelper.BuildEnv(proxyUrl, _options.NoProxy);

        var args = new List<string>();
        args.AddRange(proxyArgs);
        args.AddRange(auth.ConfigArgs);
        args.Add("ls-remote");
        args.Add(remoteUrl);

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, _options.DefaultTimeoutSeconds).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw CreateGitException("ls-remote", result, proxyUrl);

        var refs = new List<GitRemoteRef>();
        foreach (var line in result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trim = line.Trim();
            if (string.IsNullOrWhiteSpace(trim)) continue;
            var parts = trim.Split('\t');
            if (parts.Length == 2)
                refs.Add(new GitRemoteRef(parts[1].Trim(), parts[0].Trim()));
            else
            {
                var sp = trim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length == 2) refs.Add(new GitRemoteRef(sp[1], sp[0]));
            }
        }
        return refs;
    }

    // --- helpers ---

    private static Dictionary<string, string> MergeEnv(IDictionary<string, string> a, IDictionary<string, string> b)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in a) res[kv.Key] = kv.Value;
        foreach (var kv in b) res[kv.Key] = kv.Value;
        // ensure GIT_TERMINAL_PROMPT=0
        res["GIT_TERMINAL_PROMPT"] = "0";
        return res;
    }

    private static IReadOnlyList<GitDiffEntry> ParseDiff(string stdout)
    {
        var list = new List<GitDiffEntry>();
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // Format: <status>\t<path>  or for rename: R100\told\tnew
            var tabs = raw.Split('\t');
            var statusRaw = tabs[0].Trim();
            var kind = MapKind(statusRaw);
            string path;
            string? oldPath = null;
            string? score = null;

            if (statusRaw.StartsWith("R", StringComparison.Ordinal) || statusRaw.StartsWith("C", StringComparison.Ordinal))
            {
                // Score attached: e.g., R100 or R095
                score = statusRaw.Length > 1 ? statusRaw.Substring(1) : null;
                if (tabs.Length >= 3)
                {
                    oldPath = tabs[1];
                    path = tabs[2];
                }
                else if (tabs.Length == 2)
                {
                    // fallback space-separated? shouldn't happen but handle
                    path = tabs[1];
                }
                else
                {
                    path = raw;
                }
            }
            else
            {
                // A/M/D etc; second column is path
                if (tabs.Length >= 2) path = tabs[1];
                else
                {
                    // fallback: status + space + path
                    var sp = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    path = sp.Length >= 2 ? sp[1] : raw;
                }
            }

            list.Add(new GitDiffEntry(kind, path, oldPath, score));
        }
        return list;
    }

    private static GitDiffKind MapKind(string status)
    {
        if (string.IsNullOrEmpty(status)) return GitDiffKind.Unknown;
        var c = status[0];
        return c switch
        {
            'A' => GitDiffKind.Added,
            'M' => GitDiffKind.Modified,
            'D' => GitDiffKind.Deleted,
            'R' => GitDiffKind.Renamed,
            'C' => GitDiffKind.Copied,
            'T' => GitDiffKind.TypeChanged,
            'U' => GitDiffKind.Unmerged,
            _ => GitDiffKind.Unknown
        };
    }

    private static bool IsValidSha(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        var s = sha.Trim();
        if (s.Length < 7 || s.Length > 40) return false;
        return s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private static InvalidOperationException CreateGitException(string op, GitRunResult result, string? proxyUrl)
    {
        var err = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var masked = SecretMasker.Mask(err);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
            masked = SecretMasker.Mask(masked);
        return new InvalidOperationException($"git {op} failed (exit {result.ExitCode}): {masked.Trim()}");
    }
}
