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

    private const char NullChar = '\0';
    private const char RenameStatusChar = 'R';
    private const char CopyStatusChar = 'C';

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
        // Ensure parent exists; remove empty stale dir if exists but not git (git clone requires non-existing target)
        if (Directory.Exists(normalized))
        {
            if (!Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                try { Directory.Delete(normalized); } catch { }
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
        // No working tree checkout: индексация читает файлы напрямую из объектов (git show),
        // распаковка только жмёт диск и время клона.
        args.Add("--no-checkout");
        args.Add("--branch");
        args.Add(branch);
        args.Add(remoteUrl);
        args.Add(normalized);

        var env = MergeEnv(auth.Env, proxyEnv);
        var cloneTimeout = _options.CloneTimeoutSeconds > 0 ? _options.CloneTimeoutSeconds : _options.DefaultTimeoutSeconds;
        GitRunResult result;
        try
        {
            result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, cloneTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { if (Directory.Exists(normalized)) Directory.Delete(normalized, recursive: true); } catch { }
            throw;
        }
        if (result.ExitCode != 0)
        {
            try { if (Directory.Exists(normalized) && !Directory.Exists(Path.Combine(normalized, ".git"))) Directory.Delete(normalized, recursive: true); } catch { }
            throw CreateGitException("clone", result, proxyUrl);
        }
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
        AddNetworkTimeoutArgs(args);
        args.Add("fetch");
        args.Add("origin");
        args.Add(branch);
        // optional: --prune ?

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, env, _logger, ct, Math.Min(_options.DefaultTimeoutSeconds, 300)).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.StdErr.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("git fetch ref-lock race in {Repo}, retrying", normalized);
            await Task.Delay(1000, ct).ConfigureAwait(false);
            result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, env, _logger, ct, Math.Min(_options.DefaultTimeoutSeconds, 300)).ConfigureAwait(false);
        }
        if (result.ExitCode != 0)
            throw CreateGitException("fetch", result, proxyUrl);
    }

    public async Task<GitDiff> DiffAsync(string localPath, string? fromSha, string toSha, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        if (string.IsNullOrWhiteSpace(toSha))
            throw new ArgumentException("toSha must not be empty", nameof(toSha));

        if (string.IsNullOrWhiteSpace(fromSha))
        {
            var files = await LsTreeAsync(normalized, toSha!, ct).ConfigureAwait(false);
            var changed = files.Select(AddedEntry).ToList();
            return new GitDiff(null, toSha!.Trim(), changed, IsFullDiff: true);
        }

        var isAncestor = await IsAncestorAsync(normalized, fromSha!, toSha!, ct).ConfigureAwait(false);
        if (!isAncestor)
        {
            _logger.LogWarning("fromSha {From} is not ancestor of {To} in {Repo} — full diff fallback", fromSha, toSha, normalized);
            var toFiles = await LsTreeAsync(normalized, toSha!, ct).ConfigureAwait(false);
            var fromFiles = await LsTreeAsync(normalized, fromSha!, ct).ConfigureAwait(false);
            var toSet = new HashSet<string>(toFiles);
            var fromSet = new HashSet<string>(fromFiles);
            var changed = new List<GitDiffEntry>();
            AddSetDiffEntries(changed, toSet, fromSet);
            foreach (var f in toSet.Intersect(fromSet))
            {
                var diff = await GitProcessRunner.RunAsync(_options.GitExecutable, new List<string> { "diff", "--name-only", $"{fromSha}..{toSha}", "--", f }, normalized, null, _logger, ct).ConfigureAwait(false);
                if (diff.StdOut.Trim().Length > 0)
                    changed.Add(new GitDiffEntry(GitDiffKind.Modified, f, null, null));
            }
            var nameStatus = await GitProcessRunner.RunAsync(_options.GitExecutable, new List<string> { "diff", "--name-status", "-M", "-z", $"{fromSha}..{toSha}" }, normalized, null, _logger, ct).ConfigureAwait(false);
            if (nameStatus.ExitCode == 0)
            {
                var parsed = ParseNameStatusZ(nameStatus.StdOut);
                var renameMap = parsed.Where(p => p.Kind == GitDiffKind.Renamed).ToList();
                foreach (var r in renameMap)
                {
                    changed.RemoveAll(c => c.Path == r.Path || c.Path == r.OldPath);
                    changed.Add(r);
                }
            }
            return new GitDiff(fromSha!.Trim(), toSha!.Trim(), changed, IsFullDiff: true);
        }

        var zArgs = new List<string> { "diff", "--name-status", "-M", "-z", $"{fromSha}..{toSha}" };
        var zResult = await GitProcessRunner.RunAsync(_options.GitExecutable, zArgs, normalized, null, _logger, ct).ConfigureAwait(false);
        if (zResult.ExitCode != 0)
        {
            _logger.LogWarning("git diff failed {From}..{To} in {Repo}: {Err} — fallback to full", fromSha, toSha, normalized, zResult.StdErr);
            var files = await LsTreeAsync(normalized, toSha!, ct).ConfigureAwait(false);
            return new GitDiff(fromSha!.Trim(), toSha!.Trim(), files.Select(AddedEntry).ToList(), true);
        }
        var changedFiles = ParseNameStatusZ(zResult.StdOut);
        if (changedFiles.Count == 0 && !string.IsNullOrEmpty(zResult.StdOut))
        {
            // Fallback to tab-separated parser if -z produced empty but stdout non-empty (legacy)
            changedFiles = ParseDiff(zResult.StdOut);
        }
        return new GitDiff(fromSha!.Trim(), toSha!.Trim(), changedFiles, IsFullDiff: false);
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

        var args = new List<string> { "ls-tree", "-r", "-z", "--name-only", commitSha.Trim() };
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, null, _logger, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw CreateGitException("ls-tree", result, null);

        // -z: NUL-separated, безопасно для кириллицы/пробелов; fallback на newline если пусто
        var raw = result.StdOut;
        if (raw.Contains('\0'))
            return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string localPath, string commitSha, CancellationToken ct = default)
        => LsTreeAsync(localPath, commitSha, ct);

    public async Task<string> ShowFileAsync(string localPath, string filePath, string commitSha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath required", nameof(filePath));
        if (string.IsNullOrWhiteSpace(commitSha)) throw new ArgumentException("commitSha required", nameof(commitSha));
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        var spec = $"{commitSha.Trim()}:{filePath}";
        var args = new List<string> { "show", spec };
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, normalized, null, _logger, ct, _options.DefaultTimeoutSeconds, captureRawStdOut: true).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git show failed for {spec} in {normalized}: {result.StdErr}");
        return ContentEncoding.Decode(result.RawStdOut!);
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
        AddNetworkTimeoutArgs(args);
        args.Add("ls-remote");
        args.Add(remoteUrl);
        if (!string.IsNullOrWhiteSpace(branch))
            args.Add(branch!);

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, Math.Min(_options.DefaultTimeoutSeconds, 300)).ConfigureAwait(false);

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
        AddNetworkTimeoutArgs(args);
        args.Add("ls-remote");
        args.Add(remoteUrl);

        var env = MergeEnv(auth.Env, proxyEnv);
        var result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, Math.Min(_options.DefaultTimeoutSeconds, 300)).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(string remoteUrl, ProxyConfig? proxy = null, Credential? credential = null, CancellationToken ct = default)
    {
        var refs = await LsRemoteAllAsync(remoteUrl, proxy, credential, ct).ConfigureAwait(false);
        var branches = refs
            .Where(r => r.Ref.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(r => r.Ref.Substring("refs/heads/".Length))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        // Fallback: also consider remote HEAD symref if needed, but branches list is heads
        return branches;
    }

    public async Task<IReadOnlyList<string>> ListRemoteRootDirectoriesAsync(string remoteUrl, string branch, ProxyConfig? proxy = null, Credential? credential = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) throw new ArgumentException("remoteUrl required", nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("branch required", nameof(branch));

        var tmpRoot = Path.Combine(Path.GetTempPath(), "coderag-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var cloneDir = Path.Combine(tmpRoot, "repo");
        try
        {
            using var auth = GitAuthHelper.Build(credential, _protector, _options.SshKeysDir);
            var proxyUrl = GitProxyHelper.BuildProxyUrl(proxy, _protector);
            var proxyArgs = GitProxyHelper.BuildGitConfigArgs(proxyUrl);
            var proxyEnv = GitProxyHelper.BuildEnv(proxyUrl, _options.NoProxy);

            var args = new List<string>();
            args.AddRange(proxyArgs);
            args.AddRange(auth.ConfigArgs);
            AddNetworkTimeoutArgs(args);
            args.Add("clone");
            args.Add("--no-checkout");
            args.Add("--depth");
            args.Add("1");
            // filter blob:none to avoid downloading blobs for large repos (needs server support, fallback gracefully)
            args.Add("--filter=blob:none");
            args.Add("--branch");
            args.Add(branch.Trim());
            args.Add(remoteUrl);
            args.Add(cloneDir);

            var env = MergeEnv(auth.Env, proxyEnv);
            var cloneTimeout = _options.CloneTimeoutSeconds > 0 ? _options.CloneTimeoutSeconds : _options.DefaultTimeoutSeconds;
            GitRunResult result;
            try
            {
                result = await GitProcessRunner.RunAsync(_options.GitExecutable, args, null, env, _logger, ct, cloneTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true); } catch { }
                throw;
            }
            if (result.ExitCode != 0)
            {
                // Fallback without filter (some servers don't support filter)
                var args2 = new List<string>();
                args2.AddRange(proxyArgs);
                args2.AddRange(auth.ConfigArgs);
                AddNetworkTimeoutArgs(args2);
                args2.Add("clone");
                args2.Add("--no-checkout");
                args2.Add("--depth");
                args2.Add("1");
                args2.Add("--branch");
                args2.Add(branch.Trim());
                args2.Add(remoteUrl);
                args2.Add(cloneDir);
                result = await GitProcessRunner.RunAsync(_options.GitExecutable, args2, null, env, _logger, ct, cloneTimeout).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw CreateGitException("clone preview", result, proxyUrl);
                }
            }

            // Get HEAD commit then list files or tree
            var headArgs = new List<string> { "rev-parse", "HEAD" };
            var headRes = await GitProcessRunner.RunAsync(_options.GitExecutable, headArgs, cloneDir, null, _logger, ct).ConfigureAwait(false);
            if (headRes.ExitCode != 0)
                throw CreateGitException("rev-parse HEAD preview", headRes, null);
            var head = headRes.StdOut.Trim();

            // Use ls-tree --name-only HEAD without -r to get top-level entries, then also need directories via recursive
            // Approach: get all files recursively then derive root dirs
            var lsArgs = new List<string> { "ls-tree", "-r", "--name-only", head };
            var lsRes = await GitProcessRunner.RunAsync(_options.GitExecutable, lsArgs, cloneDir, null, _logger, ct).ConfigureAwait(false);
            if (lsRes.ExitCode != 0)
                throw CreateGitException("ls-tree preview", lsRes, null);
            var files = lsRes.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var roots = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                var slash = f.IndexOf('/');
                if (slash > 0)
                    roots.Add(f.Substring(0, slash));
                else
                    roots.Add(f); // top-level file is its own entry; we show it as file but could filter to dirs only? Show top-level dirs + files as directories? spec says корни directories, so include only dirs
            }
            // Filter to only directories (exclude bare files): keep those that had slash
            var dirSet = new HashSet<string>(files.Where(f => f.Contains('/')).Select(f => f.Substring(0, f.IndexOf('/'))), StringComparer.Ordinal);
            // If repo is flat (no subdirs), return empty? Better return sorted dirSet
            var resultDirs = dirSet.OrderBy(s => s, StringComparer.Ordinal).ToList();
            // If dirSet empty but files exist (flat repo), return empty list (no dirs to filter)
            return resultDirs;
        }
        finally
        {
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true); } catch { }
        }
    }

    public async Task<IReadOnlyList<string>> ListLocalRootDirectoriesAsync(string localPath, string commitOrBranch, CancellationToken ct = default)
    {
        var normalized = PathValidator.ValidateAndNormalize(localPath, _options.ReposRoot);
        if (!Directory.Exists(Path.Combine(normalized, ".git")))
            throw new InvalidOperationException($"Not a git repository: {normalized}");

        // Resolve commit: if branch name, try rev-parse origin/branch else HEAD
        string commitSha = commitOrBranch;
        if (!IsValidSha(commitOrBranch))
        {
            // Try branch
            var revArgs = new List<string> { "rev-parse", $"origin/{commitOrBranch}" };
            var revRes = await GitProcessRunner.RunAsync(_options.GitExecutable, revArgs, normalized, null, _logger, ct).ConfigureAwait(false);
            if (revRes.ExitCode == 0 && IsValidSha(revRes.StdOut.Trim()))
                commitSha = revRes.StdOut.Trim();
            else
            {
                var headRes = await GitProcessRunner.RunAsync(_options.GitExecutable, new List<string> { "rev-parse", "HEAD" }, normalized, null, _logger, ct).ConfigureAwait(false);
                if (headRes.ExitCode == 0 && IsValidSha(headRes.StdOut.Trim()))
                    commitSha = headRes.StdOut.Trim();
                else
                    throw CreateGitException("rev-parse local preview", revRes, null);
            }
        }

        var lsArgs = new List<string> { "ls-tree", "-r", "--name-only", commitSha };
        var lsRes = await GitProcessRunner.RunAsync(_options.GitExecutable, lsArgs, normalized, null, _logger, ct).ConfigureAwait(false);
        if (lsRes.ExitCode != 0)
            throw CreateGitException("ls-tree local", lsRes, null);
        var files = lsRes.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var dirSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var slash = f.IndexOf('/');
            if (slash > 0) dirSet.Add(f.Substring(0, slash));
        }
        return dirSet.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    // --- helpers ---

    /// <summary>
    /// Быстрое падение при "черной дыре" сети: TCP connect до 30s,
    /// скорость ниже 1KB/s дольше 60s = abort (иначе git может вешаться десятки минут).
    /// </summary>
    private static void AddNetworkTimeoutArgs(List<string> args)
    {
        args.Add("-c"); args.Add("http.connectTimeout=30");
        args.Add("-c"); args.Add("http.lowSpeedLimit=1024");
        args.Add("-c"); args.Add("http.lowSpeedTime=60");
    }

    private static Dictionary<string, string> MergeEnv(IDictionary<string, string> a, IDictionary<string, string> b)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in a) res[kv.Key] = kv.Value;
        foreach (var kv in b) res[kv.Key] = kv.Value;
        // ensure GIT_TERMINAL_PROMPT=0
        res["GIT_TERMINAL_PROMPT"] = "0";
        return res;
    }

    private static IReadOnlyList<GitDiffEntry> ParseNameStatusZ(string stdout)
    {
        var list = new List<GitDiffEntry>();
        var tokens = stdout.Split(NullChar);
        for (var i = 0; i < tokens.Length; i++)
        {
            var status = tokens[i].Trim();
            if (status.Length == 0) continue;
            var kind = MapKind(status);
            string? score = null;
            if (status.Length > 1 && (status[0] == RenameStatusChar || status[0] == CopyStatusChar))
                score = status.Substring(1);
            if ((kind == GitDiffKind.Renamed || kind == GitDiffKind.Copied) && i + 2 < tokens.Length)
            {
                list.Add(new GitDiffEntry(kind, tokens[i + 2], tokens[i + 1], score));
                i += 2;
            }
            else if (i + 1 < tokens.Length)
            {
                list.Add(new GitDiffEntry(kind, tokens[i + 1], null, score));
                i += 1;
            }
        }
        return list;
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

            if (statusRaw.Length > 0 && (statusRaw[0] == RenameStatusChar || statusRaw[0] == CopyStatusChar))
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

    private static GitDiffEntry AddedEntry(string path) => new(GitDiffKind.Added, path, null, null);

    private static void AddSetDiffEntries(List<GitDiffEntry> changed, HashSet<string> toSet, HashSet<string> fromSet)
    {
        foreach (var f in toSet.Except(fromSet)) changed.Add(AddedEntry(f));
        foreach (var f in fromSet.Except(toSet)) changed.Add(new GitDiffEntry(GitDiffKind.Deleted, f, null, null));
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
            RenameStatusChar => GitDiffKind.Renamed,
            CopyStatusChar => GitDiffKind.Copied,
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
