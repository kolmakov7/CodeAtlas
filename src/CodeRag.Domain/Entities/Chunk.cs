using System.Security.Cryptography;
using System.Text;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>
/// Семантический чанк (таблица chunks). Границы — из анализатора; fallback 800 токенов.
/// Инварианты:
/// - Source не пуст, ContentHash = sha256(Source).
/// - Embedding-поля опциональны; переэмбеддинг если ContentHash/Model/ConfigHash изменился.
/// </summary>
public sealed class Chunk
{
    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public Guid FileId { get; private set; }
    public Guid? SymbolId { get; private set; }

    public string? Language { get; private set; }
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }

    public string Source { get; private set; }
    public string ContentHash { get; private set; }

    public string? EmbeddingModel { get; private set; }
    public string? EmbeddingConfigHash { get; private set; }
    public DateTimeOffset? EmbeddedAt { get; private set; }
    /// <summary>Which embedding space this chunk belongs to (code vs text). Persisted as "code"/"text".</summary>
    public EmbeddingDomain EmbeddingDomain { get; private set; } = EmbeddingDomain.Code;

    private Chunk(Guid id, Guid repositoryId, Guid fileId, string source, string contentHash)
    {
        Id = id;
        RepositoryId = repositoryId;
        FileId = fileId;
        Source = source;
        ContentHash = contentHash;
    }

    public static Chunk Create(
        Guid id, Guid repositoryId, Guid fileId, string source,
        Guid? symbolId = null, string? language = null,
        int? startLine = null, int? endLine = null,
        string? embeddingModel = null, string? embeddingConfigHash = null,
        EmbeddingDomain? domain = null, DateTimeOffset? embeddedAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Chunk Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");
        if (fileId == Guid.Empty) throw new DomainValidationException("FileId must not be empty.");
        if (string.IsNullOrWhiteSpace(source)) throw new DomainValidationException("Chunk Source must not be empty.");
        if (startLine is not null && startLine < 1) throw new DomainValidationException("StartLine must be >=1.");
        if (endLine is not null && endLine < 1) throw new DomainValidationException("EndLine must be >=1.");
        if (startLine is not null && endLine is not null && endLine < startLine)
            throw new DomainValidationException("EndLine must be >= StartLine.");

        var hash = ComputeHash(source);
        var resolved = domain ?? DetectDomain(language, source);

        return new Chunk(id, repositoryId, fileId, source, hash)
        {
            SymbolId = symbolId,
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            StartLine = startLine,
            EndLine = endLine,
            EmbeddingModel = string.IsNullOrWhiteSpace(embeddingModel) ? null : embeddingModel.Trim(),
            EmbeddingConfigHash = string.IsNullOrWhiteSpace(embeddingConfigHash) ? null : embeddingConfigHash.Trim(),
            EmbeddingDomain = resolved,
            EmbeddedAt = embeddedAt
        };
    }

    /// <summary>Lightweight fallback classifier (path-based classification lives in the Indexer's ContentClassifier).</summary>
    private static EmbeddingDomain DetectDomain(string? language, string source)
    {
        // Simple heuristics: markdown/docs → Text, everything else (source code, comments) → Code.
        if (language != null)
        {
            var lower = language.ToLowerInvariant();
            if (lower is "markdown" or "md" or "txt" or "rst")
                return EmbeddingDomain.Text;
        }
        if (source != null)
        {
            var lowerSource = source.ToLowerInvariant();
            if (lowerSource.StartsWith("readme") || lowerSource.StartsWith("documentation"))
                return EmbeddingDomain.Text;
        }
        return EmbeddingDomain.Code; // default
    }

    public static Chunk Rehydrate(
        Guid id, Guid repositoryId, Guid fileId, string source, string contentHash,
        Guid? symbolId, string? language, int? startLine, int? endLine,
        string? embeddingModel, string? embeddingConfigHash, string? embeddingDomain, DateTimeOffset? embeddedAt)
    {
        var hash = string.IsNullOrWhiteSpace(contentHash) ? ComputeHash(source) : contentHash;
        var chunk = new Chunk(id, repositoryId, fileId, source, hash)
        {
            SymbolId = symbolId,
            Language = language,
            StartLine = startLine,
            EndLine = endLine,
            EmbeddingModel = embeddingModel,
            EmbeddingConfigHash = embeddingConfigHash,
            EmbeddingDomain = embeddingDomain.ToDomain(),
            EmbeddedAt = embeddedAt
        };
        return chunk;
    }

    /// <summary>Sha256 hex нижнего регистра по UTF8.</summary>
    public static string ComputeHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void UpdateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new DomainValidationException("Chunk Source must not be empty.");
        Source = source;
        ContentHash = ComputeHash(source);
        // инвалидируем эмбеддинг — контент изменился
        EmbeddedAt = null;
    }

    public void MarkEmbedded(string model, string configHash, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new DomainValidationException("EmbeddingModel must not be empty.");
        if (string.IsNullOrWhiteSpace(configHash)) throw new DomainValidationException("EmbeddingConfigHash must not be empty.");
        EmbeddingModel = model.Trim();
        EmbeddingConfigHash = configHash.Trim();
        EmbeddedAt = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Нужен ли переэмбеддинг (indexing.md §5).</summary>
    public bool NeedsEmbedding(string currentModel, string currentConfigHash) =>
        EmbeddedAt is null ||
        !string.Equals(EmbeddingModel, currentModel, StringComparison.Ordinal) ||
        !string.Equals(EmbeddingConfigHash, currentConfigHash, StringComparison.Ordinal);

    public bool IsContentEqual(string source) =>
        string.Equals(ContentHash, ComputeHash(source), StringComparison.Ordinal);

    internal static Chunk Rehydrate(
        Guid id, Guid repositoryId, Guid fileId, string source, string contentHash,
        Guid? symbolId, string? language, int? startLine, int? endLine,
        string? embeddingModel, string? embeddingConfigHash, DateTimeOffset? embeddedAt)
    {
        var hash = string.IsNullOrWhiteSpace(contentHash) ? ComputeHash(source) : contentHash;
        return new Chunk(id, repositoryId, fileId, source, hash)
        {
            SymbolId = symbolId,
            Language = language,
            StartLine = startLine,
            EndLine = endLine,
            EmbeddingModel = embeddingModel,
            EmbeddingConfigHash = embeddingConfigHash,
            EmbeddedAt = embeddedAt
        };
    }
}
