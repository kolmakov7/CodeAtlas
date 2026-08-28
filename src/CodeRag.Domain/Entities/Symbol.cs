using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Символ кода (таблица symbols). Границы — из анализатора.</summary>
public sealed class Symbol
{
    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid FileId { get; private set; }

    public string Language { get; private set; }
    public SymbolKind Kind { get; private set; }
    public string Name { get; private set; }
    public string? Fqn { get; private set; }
    public string? Namespace { get; private set; }

    public Guid? ParentSymbolId { get; private set; }

    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }
    public int? StartCol { get; private set; }
    public int? EndCol { get; private set; }

    public string? Signature { get; private set; }

    private Symbol(Guid id, Guid repositoryId, Guid fileId, string language, SymbolKind kind, string name)
    {
        Id = id;
        RepositoryId = repositoryId;
        FileId = fileId;
        Language = language;
        Kind = kind;
        Name = name;
    }

    public static Symbol Create(
        Guid id, Guid repositoryId, Guid fileId,
        string language, SymbolKind kind, string name,
        string? fqn = null, string? @namespace = null,
        Guid? parentSymbolId = null, Guid? projectId = null,
        int? startLine = null, int? endLine = null,
        int? startCol = null, int? endCol = null,
        string? signature = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Symbol Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");
        if (fileId == Guid.Empty) throw new DomainValidationException("FileId must not be empty.");
        if (string.IsNullOrWhiteSpace(language)) throw new DomainValidationException("Language must not be empty.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Symbol Name must not be empty.");
        if (startLine is not null && startLine < 1) throw new DomainValidationException("StartLine must be >=1.");
        if (endLine is not null && endLine < 1) throw new DomainValidationException("EndLine must be >=1.");
        if (startLine is not null && endLine is not null && endLine < startLine)
            throw new DomainValidationException("EndLine must be >= StartLine.");

        return new Symbol(id, repositoryId, fileId, language.Trim(), kind, name.Trim())
        {
            Fqn = string.IsNullOrWhiteSpace(fqn) ? null : fqn.Trim(),
            Namespace = string.IsNullOrWhiteSpace(@namespace) ? null : @namespace.Trim(),
            ParentSymbolId = parentSymbolId,
            ProjectId = projectId,
            StartLine = startLine,
            EndLine = endLine,
            StartCol = startCol,
            EndCol = endCol,
            Signature = string.IsNullOrWhiteSpace(signature) ? null : signature.Trim()
        };
    }
}
