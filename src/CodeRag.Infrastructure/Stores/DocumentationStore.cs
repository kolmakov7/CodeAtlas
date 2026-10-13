using System.Data;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Stores;

public sealed class DocumentationStore : IDocumentationStore
{
    private readonly IDbConnectionFactory _factory;
    public DocumentationStore(IDbConnectionFactory factory) => _factory = factory;

    private static string StatusToText(DocumentationStatus s) => s.ToString().ToLowerInvariant();

    private static DocumentationStatus StatusFromText(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<DocumentationStatus>(raw.Trim(), ignoreCase: true, out var s)
            ? s : DocumentationStatus.Generated;

    private static DocumentationTargetType TypeFromText(string? raw) =>
        DocumentationTargetTypeExtensions.TryParseType(raw, out var t)
            ? t : DocumentationTargetType.Symbol;

    private static DocRowParams Params(Documentation d) => new()
    {
        id = d.Id.ToString(),
        repo = d.RepositoryId.ToString(),
        type = d.TargetType.ToText(),
        targetId = d.TargetId?.ToString(),
        targetRef = d.TargetRef,
        name = d.Name,
        ns = d.Namespace,
        filePath = d.FilePath,
        lang = d.Language,
        content = d.Content,
        model = d.Model,
        promptVersion = d.PromptVersion,
        sourceHash = d.SourceHash,
        status = StatusToText(d.Status),
        error = d.Error,
        commit = d.GenerationCommit,
        ctxChars = d.ContextChars,
        outChars = d.OutputChars,
        generatedAt = d.GeneratedAt.ToString("o"),
        updatedAt = d.UpdatedAt.ToString("o")
    };

    public async Task UpsertAsync(Documentation doc, CancellationToken ct = default)
    {
        var ns = SqlDialect.QuoteIdent(_factory, "namespace");
        var columns = $"id, repository_id, target_type, target_id, target_ref, name, {ns}, file_path, language, content, model, prompt_version, source_hash, status, error, generation_commit, context_chars, output_chars, generated_at, updated_at";
        var values = "@id,@repo,@type,@targetId,@targetRef,@name,@ns,@filePath,@lang,@content,@model,@promptVersion,@sourceHash,@status,@error,@commit,@ctxChars,@outChars,@generatedAt,@updatedAt";
        var updateSet =
            "repository_id=excluded.repository_id, target_type=excluded.target_type, target_id=excluded.target_id, " +
            $"target_ref=excluded.target_ref, name=excluded.name, {ns}=excluded.{ns}, file_path=excluded.file_path, " +
            "language=excluded.language, content=excluded.content, model=excluded.model, " +
            "prompt_version=excluded.prompt_version, source_hash=excluded.source_hash, status=excluded.status, " +
            "error=excluded.error, generation_commit=excluded.generation_commit, context_chars=excluded.context_chars, " +
            "output_chars=excluded.output_chars, generated_at=excluded.generated_at, updated_at=excluded.updated_at";

        var p = Params(doc);
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        // Primary identity: the row's own id.
        var byId = $"INSERT INTO documentation({columns}) VALUES ({values}) ON CONFLICT(id) DO UPDATE SET {updateSet};";
        try
        {
            await conn.ExecuteAsync(byId, p).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlDialect.IsUniqueViolation(_factory, ex))
        {
            // Stable identity already exists under a different id — update that row, keep its id.
            var byTarget = $"INSERT INTO documentation({columns}) VALUES ({values}) ON CONFLICT(repository_id, target_type, target_ref) DO UPDATE SET {updateSet};";
            await conn.ExecuteAsync(byTarget, p).ConfigureAwait(false);
        }
    }

    public async Task<Documentation?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var ns = SqlDialect.QuoteIdent(_factory, "namespace");
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<DocRow>(
            $"SELECT * FROM documentation WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<Documentation?> GetByTargetAsync(
        Guid repositoryId, DocumentationTargetType targetType, string targetRef, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<DocRow>(
            @"SELECT * FROM documentation
              WHERE repository_id=@repo AND target_type=@type AND target_ref=@targetRef
              LIMIT 1;",
            new { repo = repositoryId.ToString(), type = targetType.ToText(), targetRef }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Documentation>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<DocRow>(
            @"SELECT * FROM documentation
              WHERE repository_id=@repo
              ORDER BY target_type, updated_at DESC, name;",
            new { repo = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Documentation>> GetByRepositoryAndTypeAsync(
        Guid repositoryId, DocumentationTargetType targetType, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<DocRow>(
            @"SELECT * FROM documentation
              WHERE repository_id=@repo AND target_type=@type
              ORDER BY updated_at DESC, name;",
            new { repo = repositoryId.ToString(), type = targetType.ToText() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM documentation WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM documentation WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
    }

    public async Task MarkStaleByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await conn.ExecuteAsync(
            @"UPDATE documentation
              SET status=@stale, updated_at=@now
              WHERE repository_id=@repo AND status NOT IN (@generated, @failed);",
            new
            {
                stale = StatusToText(DocumentationStatus.Stale),
                generated = StatusToText(DocumentationStatus.Generated),
                failed = StatusToText(DocumentationStatus.Failed),
                now,
                repo = repositoryId.ToString()
            }).ConfigureAwait(false);
    }

    private static Documentation Map(DocRow r)
    {
        return Documentation.Rehydrate(
            Guid.Parse(r.id),
            Guid.Parse(r.repository_id),
            TypeFromText(r.target_type),
            r.target_ref,
            r.name,
            string.IsNullOrWhiteSpace(r.target_id) ? null : Guid.Parse(r.target_id),
            r.@namespace,
            r.file_path,
            r.language,
            r.content,
            r.model,
            r.prompt_version,
            r.source_hash,
            StatusFromText(r.status),
            r.error,
            r.generation_commit,
            r.context_chars,
            r.output_chars,
            DateTimeOffset.Parse(r.generated_at),
            DateTimeOffset.Parse(r.updated_at));
    }

    private sealed class DocRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string target_type { get; set; } = "";
        public string? target_id { get; set; }
        public string target_ref { get; set; } = "";
        public string name { get; set; } = "";
        public string? @namespace { get; set; }
        public string? file_path { get; set; }
        public string? language { get; set; }
        public string content { get; set; } = "";
        public string? model { get; set; }
        public string prompt_version { get; set; } = "v1";
        public string? source_hash { get; set; }
        public string status { get; set; } = "";
        public string? error { get; set; }
        public string? generation_commit { get; set; }
        public int? context_chars { get; set; }
        public int? output_chars { get; set; }
        public string generated_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class DocRowParams
    {
        public string id { get; init; } = "";
        public string repo { get; init; } = "";
        public string type { get; init; } = "";
        public string? targetId { get; init; }
        public string targetRef { get; init; } = "";
        public string name { get; init; } = "";
        public string? ns { get; init; }
        public string? filePath { get; init; }
        public string? lang { get; init; }
        public string content { get; init; } = "";
        public string? model { get; init; }
        public string promptVersion { get; init; } = "v1";
        public string? sourceHash { get; init; }
        public string status { get; init; } = "";
        public string? error { get; init; }
        public string? commit { get; init; }
        public int? ctxChars { get; init; }
        public int? outChars { get; init; }
        public string generatedAt { get; init; } = "";
        public string updatedAt { get; init; } = "";
    }
}
