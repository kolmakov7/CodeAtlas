using System.Diagnostics;
using System.Text;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;

namespace CodeRag.Git.Auth;

/// <summary>Помощь для применения Auth per-repository (§59): credential.helper, GIT_ASKPASS, GIT_SSH_COMMAND.</summary>
public sealed class GitAuthContext : IDisposable
{
    public List<string> ConfigArgs { get; } = new();
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _tempFiles = new();

    public void AddTempFile(string path) => _tempFiles.Add(path);

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}

public static class GitAuthHelper
{
    /// <summary>
    /// Строит GitAuthContext для указанного credential.
    /// - HttpsToken/UserPass via credential.helper + GIT_ASKPASS temp script
    /// - SshKey via GIT_SSH_COMMAND="ssh -i /data/keys/ssh/&lt;id&gt; -o StrictHostKeyChecking=accept-new"
    /// Ключ кладётся в /data/keys/ssh/&lt;credentialId&gt; с chmod 600.
    /// </summary>
    public static GitAuthContext Build(
        Credential? credential,
        ICredentialProtector protector,
        string sshKeysDir = "/data/keys/ssh")
    {
        var ctx = new GitAuthContext();

        if (credential is null) return ctx;

        switch (credential.Kind)
        {
            case CredentialKind.HttpsToken:
                BuildHttpsToken(ctx, credential, protector);
                break;
            case CredentialKind.HttpsUserPass:
                BuildUserPass(ctx, credential, protector);
                break;
            case CredentialKind.SshKey:
                BuildSshKey(ctx, credential, protector, sshKeysDir);
                break;
        }

        return ctx;
    }

    private static void BuildHttpsToken(GitAuthContext ctx, Credential cred, ICredentialProtector protector)
    {
        var token = cred.SecretEncrypted is null ? string.Empty : protector.Unprotect(cred.SecretEncrypted);
        var username = string.IsNullOrWhiteSpace(cred.Username) ? "oauth2" : cred.Username!.Trim();

        if (!string.IsNullOrWhiteSpace(token))
        {
            // Primary: credential.helper via -c
            // git -c credential.helper='!f() { echo "username=$USER"; echo "password=$TOKEN"; }; f'
            // Нужно экранировать одинарные кавычки.
            var helper = BuildCredentialHelper(username, token);
            ctx.ConfigArgs.Add("-c");
            ctx.ConfigArgs.Add(helper);

            // Also support Authorization Bearer header (для GitHub PAT как Bearer)
            ctx.ConfigArgs.Add("-c");
            ctx.ConfigArgs.Add($"http.extraHeader=Authorization: Bearer {token}");

            // Дополнительно GIT_ASKPASS temp script для fallback
            var askPass = CreateAskPassScript(username, token);
            if (askPass is not null)
            {
                ctx.Env["GIT_ASKPASS"] = askPass;
                ctx.AddTempFile(askPass);
                ctx.Env["GIT_USERNAME"] = username;
                ctx.Env["GIT_PASSWORD"] = token;
            }
        }
    }

    private static void BuildUserPass(GitAuthContext ctx, Credential cred, ICredentialProtector protector)
    {
        var user = cred.Username ?? string.Empty;
        var pass = cred.SecretEncrypted is null ? string.Empty : protector.Unprotect(cred.SecretEncrypted);

        if (!string.IsNullOrWhiteSpace(user) || !string.IsNullOrWhiteSpace(pass))
        {
            var helper = BuildCredentialHelper(user, pass);
            ctx.ConfigArgs.Add("-c");
            ctx.ConfigArgs.Add(helper);

            var askPass = CreateAskPassScript(user, pass);
            if (askPass is not null)
            {
                ctx.Env["GIT_ASKPASS"] = askPass;
                ctx.AddTempFile(askPass);
            }
        }
    }

    private static void BuildSshKey(GitAuthContext ctx, Credential cred, ICredentialProtector protector, string sshKeysDir)
    {
        string? keyPath = null;

        if (!string.IsNullOrWhiteSpace(cred.SshKeyPath) && File.Exists(cred.SshKeyPath!))
        {
            keyPath = cred.SshKeyPath!;
        }
        else if (!string.IsNullOrWhiteSpace(cred.SecretEncrypted))
        {
            // Inline key content — записать в /data/keys/ssh/<id>
            Directory.CreateDirectory(sshKeysDir);
            var plain = protector.Unprotect(cred.SecretEncrypted!);
            // Detect if plain already is a path or key material
            if (plain.Contains("BEGIN") && plain.Contains("PRIVATE KEY"))
            {
                keyPath = Path.Combine(sshKeysDir, cred.Id.ToString());
                File.WriteAllText(keyPath, plain, new UTF8Encoding(false));
                SetSecurePermissions(keyPath);
            }
            else if (File.Exists(plain))
            {
                keyPath = plain;
            }
            else
            {
                // Treat as raw key
                keyPath = Path.Combine(sshKeysDir, cred.Id.ToString());
                File.WriteAllText(keyPath, plain, new UTF8Encoding(false));
                SetSecurePermissions(keyPath);
            }
        }
        else if (!string.IsNullOrWhiteSpace(cred.SshKeyPath))
        {
            // path from DB may not yet exist on FS but directory exists
            keyPath = cred.SshKeyPath!;
        }

        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            // Ensure chmod 600 even for existing file
            try { SetSecurePermissions(keyPath!); } catch { }

            var sshCmd = $"ssh -i {EscapeShellArg(keyPath!)} -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes -o BatchMode=yes";
            ctx.Env["GIT_SSH_COMMAND"] = sshCmd;
            // Also protect from interactive prompt
            ctx.Env["GIT_SSH_VARIANT"] = "ssh";
        }
    }

    private static string BuildCredentialHelper(string username, string password)
    {
        // Escape single quotes and double quotes for shell.
        // Use: credential.helper='!f() { echo "username=..."; echo "password=..."; }; f'
        // Username/password may contain special characters — escape double quotes and backslashes inside.
        string EscapeForEcho(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");

        var u = EscapeForEcho(username);
        var p = EscapeForEcho(password);

        return $"credential.helper=!f() {{ echo \"username={u}\"; echo \"password={p}\"; }}; f";
    }

    private static string? CreateAskPassScript(string username, string password)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"git-askpass-{Guid.NewGuid():N}.sh");
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            // Git calls askpass with prompt like "Username for 'https://github.com': " or "Password for ..."
            sb.AppendLine("case \"$1\" in");
            sb.AppendLine($"  *Username*|*username*) echo \"{EscapeShellArgSimple(username)}\" ;;");
            sb.AppendLine($"  *Password*|*password*) echo \"{EscapeShellArgSimple(password)}\" ;;");
            sb.AppendLine($"  *) echo \"{EscapeShellArgSimple(password)}\" ;;");
            sb.AppendLine("esac");
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            // chmod 700
            try
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Fallback to chmod via process
                try
                {
                    var psi = new ProcessStartInfo("chmod", $"700 \"{tmp}\"") { UseShellExecute = false };
                    Process.Start(psi)?.WaitForExit(5000);
                }
                catch { }
            }
            return tmp;
        }
        catch
        {
            return null;
        }
    }

    private static void SetSecurePermissions(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo("chmod", $"600 \"{path}\"") { UseShellExecute = false };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { }
        }
    }

    private static string EscapeShellArg(string s)
    {
        // Simple shell escaping: single-quote wrap
        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }

    private static string EscapeShellArgSimple(string s)
    {
        // For inside double quotes in script: escape double quotes
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
