using System.Diagnostics;
using System.Text;
using CodeRag.Git.Helpers;
using Microsoft.Extensions.Logging;

namespace CodeRag.Git.Helpers;

public sealed record GitRunResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Низкоуровневый запуск git с env и обработкой ошибок.</summary>
public static class GitProcessRunner
{
    public static async Task<GitRunResult> RunAsync(
        string gitExecutable,
        IReadOnlyList<string> args,
        string? workingDirectory,
        IDictionary<string, string>? envOverrides,
        ILogger? logger,
        CancellationToken ct,
        int timeoutSeconds = 120)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Build argument list safely
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        // Mandatory env to avoid interactive prompts
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = psi.Environment.ContainsKey("GIT_ASKPASS") ? psi.Environment["GIT_ASKPASS"] : string.Empty;

        // Remove empty GIT_ASKPASS default if not set later
        if (string.IsNullOrEmpty(psi.Environment["GIT_ASKPASS"]))
            psi.Environment.Remove("GIT_ASKPASS");

        if (envOverrides is not null)
        {
            foreach (var kv in envOverrides)
            {
                if (kv.Value is null) continue;
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        // Ensure GIT_TERMINAL_PROMPT=0 unless explicitly overridden
        if (!psi.Environment.ContainsKey("GIT_TERMINAL_PROMPT"))
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var masked = SecretMasker.Mask($"{gitExecutable} {string.Join(" ", args.Select(EscapeArg))}");
        logger?.LogDebug("Running git: {Cmd}", masked);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!proc.Start())
                throw new InvalidOperationException("Failed to start git process.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"git timed out after {timeoutSeconds}s: {masked}");
            }

            var result = new GitRunResult(proc.ExitCode, stdout.ToString(), stderr.ToString());

            if (result.ExitCode != 0)
            {
                logger?.LogDebug("git exit {Code}: {Err}", result.ExitCode, SecretMasker.Mask(result.StdErr));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
        {
            logger?.LogError(ex, "git failed: {Cmd}", masked);
            throw;
        }
    }

    private static string EscapeArg(string a)
    {
        if (a.Contains(' ') || a.Contains('"') || a.Contains('\''))
            return $"\"{a}\"";
        return a;
    }
}
