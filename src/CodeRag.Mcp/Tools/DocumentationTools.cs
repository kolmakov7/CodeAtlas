using System.ComponentModel;
using System.Text;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation;
using CodeRag.Documentation.Models;
using CodeRag.Indexer;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DocumentationEntity = CodeRag.Domain.Entities.Documentation;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class DocumentationTools
{
    private readonly DocumentationService _docs;
    private readonly IDocumentationStore _store;
    private readonly IRepositoryStore _repoStore;
    private readonly ISymbolStore _symbols;
    private readonly IFileStore _files;
    private readonly IJobRunner _jobRunner;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<DocumentationTools> _logger;
    private const int MaxOutputLines = 400;

    public DocumentationTools(
        DocumentationService docs,
        IDocumentationStore store,
        IRepositoryStore repoStore,
        ISymbolStore symbols,
        IFileStore files,
        IJobRunner jobRunner,
        ILogger<DocumentationTools> logger,
        IMcpUsageRecorder? usage = null)
    {
        _docs = docs;
        _store = store;
        _repoStore = repoStore;
        _symbols = symbols;
        _files = files;
        _jobRunner = jobRunner;
        _logger = logger;
        _usage = usage;
    }

    private async Task<string> TrackAsync(string tool, Guid? repositoryId, Func<Task<string>> execute)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try { result = await execute().ConfigureAwait(false); }
        catch (Exception ex) { result = $"Error: {ex.Message}"; }
        sw.Stop();
        _usage?.Record(tool, !result.StartsWith("Error:", StringComparison.Ordinal), (int)sw.ElapsedMilliseconds, repositoryId);
        return result;
    }

    [McpServerTool, Description("Generate technical documentation (in English, as Markdown) for a target, built from the existing index (symbols, relations, source). targetType: 'repository' (whole repo, runs as a background job — returns a jobId to poll), 'module' (a directory, use targetRef as the directory path), or 'symbol' (a class/interface/record/enum, use symbolId). force=true regenerates even if up to date. For repository scope, track progress via the jobs API or documentation status.")]
    public Task<string> GenerateDocumentationAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("Target type: repository|module|symbol")] string targetType,
        [Description("Symbol Id (Guid) — required when targetType=symbol")] Guid? symbolId = null,
        [Description("Directory path relative to repo root — required when targetType=module")] string? targetRef = null,
        [Description("Regenerate even if the target is already up to date")] bool force = false,
        CancellationToken ct = default)
        => TrackAsync("generate_documentation", repositoryId,
            () => GenerateDocumentationCoreAsync(repositoryId, targetType, symbolId, targetRef, force, ct));

    private async Task<string> GenerateDocumentationCoreAsync(
        Guid repositoryId, string targetType, Guid? symbolId, string? targetRef, bool force, CancellationToken ct)
    {
        if (!DocumentationTargetTypeExtensions.TryParseType(targetType, out var type))
            return "Error: targetType must be repository, module or symbol";

        var repo = await _repoStore.GetByIdAsync(repositoryId, ct).ConfigureAwait(false);
        if (repo is null) return $"Error: repository {repositoryId} not found";

        if (type == DocumentationTargetType.Repository)
        {
            var options = System.Text.Json.JsonSerializer.Serialize(new { force });
            var job = IndexingJob.Create(Guid.NewGuid(), repositoryId, JobType.Documentation, options: options);
            await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
            return $"Documentation generation queued for repository '{repo.Name}' (jobId={job.Id}). Poll the jobs endpoint or documentation status for progress.";
        }

        var target = type == DocumentationTargetType.Symbol
            ? await BuildSymbolTargetAsync(repo, symbolId, ct)
            : BuildModuleTarget(repo, targetRef);
        if (target is null) return type == DocumentationTargetType.Symbol
            ? "Error: symbolId is required and must exist for symbol targets"
            : "Error: targetRef (directory path) is required for module targets";

        var result = await _docs.GenerateForTargetAsync(target, force, ct).ConfigureAwait(false);
        if (result.Documentation is null)
            return $"Error: {result.Error ?? "documentation generation failed"}";

        return FormatResult(result.Documentation, result.Generated);
    }

    [McpServerTool(Name = "generate_documentation"), Description("Alias for GenerateDocumentation")]
    public Task<string> GenerateDocumentation(Guid repositoryId, string targetType, Guid? symbolId = null, string? targetRef = null, bool force = false)
        => GenerateDocumentationAsync(repositoryId, targetType, symbolId, targetRef, force);

    [McpServerTool, Description("Get the generated documentation (Markdown) for a target. targetType: repository|module|symbol. For repository scope use targetRef='repository'; for module use the directory path; for symbol you may pass symbolId or the symbol FQN as targetRef. Returns the content plus status and metadata, or a note when it has not been generated yet.")]
    public Task<string> GetDocumentationAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("Target type: repository|module|symbol")] string targetType,
        [Description("Target ref: 'repository' | directory path | symbol FQN")] string targetRef,
        CancellationToken ct = default)
        => TrackAsync("get_documentation", repositoryId,
            () => GetDocumentationCoreAsync(repositoryId, targetType, targetRef, ct));

    private async Task<string> GetDocumentationCoreAsync(Guid repositoryId, string targetType, string targetRef, CancellationToken ct)
    {
        if (!DocumentationTargetTypeExtensions.TryParseType(targetType, out var type))
            return "Error: targetType must be repository, module or symbol";
        if (string.IsNullOrWhiteSpace(targetRef)) return "Error: targetRef required";

        var doc = await _store.GetByTargetAsync(repositoryId, type, targetRef, ct).ConfigureAwait(false);
        if (doc is null)
            return $"No documentation generated yet for {type.ToText()} '{targetRef}'. Use generate_documentation first.";
        return FormatResult(doc, generated: false);
    }

    [McpServerTool(Name = "get_documentation"), Description("Alias for GetDocumentation")]
    public Task<string> GetDocumentation(Guid repositoryId, string targetType, string targetRef)
        => GetDocumentationAsync(repositoryId, targetType, targetRef);

    [McpServerTool, Description("List all generated documentation entries for a repository with their lifecycle status (generated|stale|generating|failed) and staleness/error reason. Use to discover what is documented and what needs regeneration. Optional targetType filter (repository|module|symbol).")]
    public Task<string> GetDocumentationStatusAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("Optional target type filter: repository|module|symbol")] string? targetType = null,
        CancellationToken ct = default)
        => TrackAsync("get_documentation_status", repositoryId,
            () => GetDocumentationStatusCoreAsync(repositoryId, targetType, ct));

    private async Task<string> GetDocumentationStatusCoreAsync(Guid repositoryId, string? targetType, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(repositoryId, ct).ConfigureAwait(false);
        if (repo is null)
            return $"Error: repository {repositoryId} not found";

        IReadOnlyList<DocumentationEntity> docs = string.IsNullOrWhiteSpace(targetType)
            ? await _store.GetByRepositoryAsync(repositoryId, ct).ConfigureAwait(false)
            : await _store.GetByRepositoryAndTypeAsync(repositoryId, DocumentationTargetTypeExtensions.TryParseType(targetType, out var t) ? t : DocumentationTargetType.Symbol, ct).ConfigureAwait(false);

        if (docs.Count == 0)
            return $"No documentation generated yet for repository '{repo.Name}'. Use generate_documentation to create it.";

        var sb = new StringBuilder();
        sb.AppendLine($"Documentation for '{repo.Name}' ({docs.Count} entries):");
        foreach (var d in docs)
        {
            sb.AppendLine($"- [{d.Status.ToString().ToLowerInvariant()}] {d.TargetType.ToText()} {d.Name} ref={d.TargetRef}");
            if (d.Status == DocumentationStatus.Stale) sb.AppendLine($"    stale: {d.Error ?? "source changed since generation"}");
            else if (d.Status == DocumentationStatus.Failed && d.Error is not null) sb.AppendLine($"    error: {d.Error}");
        }
        return TruncateLines(sb.ToString(), MaxOutputLines);
    }

    [McpServerTool(Name = "get_documentation_status"), Description("Alias for GetDocumentationStatus")]
    public Task<string> GetDocumentationStatus(Guid repositoryId, string? targetType = null)
        => GetDocumentationStatusAsync(repositoryId, targetType);

    // ------------------------------------------------------------------ Helpers

    private async Task<DocumentationTarget?> BuildSymbolTargetAsync(
        CodeRag.Domain.Entities.Repository repo, Guid? symbolId, CancellationToken ct)
    {
        if (symbolId is not Guid sid) return null;
        var symbol = await _symbols.GetByIdAsync(sid, ct).ConfigureAwait(false);
        if (symbol is null) return null;
        var file = await _files.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
        return new DocumentationTarget(
            DocumentationTargetType.Symbol, repo.Id,
            DocumentationTarget.BuildTargetRef(DocumentationTargetType.Symbol, symbol.Fqn, symbol.Name, null, null),
            symbol.Name,
            SymbolId: symbol.Id,
            RepositoryName: repo.Name,
            Namespace: symbol.Namespace,
            FilePath: file?.Path,
            Language: symbol.Language,
            IndexedCommit: repo.IndexedCommit,
            Branch: repo.Branch);
    }

    private static DocumentationTarget BuildModuleTarget(
        CodeRag.Domain.Entities.Repository repo, string? targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef)) return null!;
        var moduleRef = DocumentationTarget.NormalizePath(targetRef);
        return new DocumentationTarget(
            DocumentationTargetType.Module, repo.Id, moduleRef, moduleRef,
            RepositoryName: repo.Name,
            IndexedCommit: repo.IndexedCommit,
            Branch: repo.Branch);
    }

    private static string FormatResult(DocumentationEntity d, bool generated)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Documentation ({d.Status.ToString().ToLowerInvariant()}{(generated ? ", just generated" : "")}): {d.TargetType.ToText()} {d.Name} ref={d.TargetRef}");
        if (d.Status == DocumentationStatus.Stale) sb.AppendLine($"note: stale — {d.Error ?? "source changed since generation"}");
        if (d.Status == DocumentationStatus.Failed && d.Error is not null) sb.AppendLine($"error: {d.Error}");
        sb.AppendLine();
        sb.AppendLine(d.Content);
        return TruncateLines(sb.ToString(), MaxOutputLines);
    }

    private static string TruncateLines(string s, int maxLines)
    {
        var lines = s.Split('\n');
        if (lines.Length <= maxLines) return s;
        return string.Join("\n", lines.Take(maxLines)) + $"\n[truncated to {maxLines} lines]";
    }
}
