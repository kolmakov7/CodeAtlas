using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;

namespace CodeRag.Indexer.Services.Handlers;

/// <summary>
/// Стратегия для <see cref="JobType"/> — каждый тип джобы вынесен в отдельный handler (SRP, OCP).
/// Добавление нового типа требует только нового класса, без изменения <see cref="IndexingService"/>.
/// </summary>
public interface IIndexingJobHandler
{
    JobType JobType { get; }
    Task ExecuteAsync(IndexingJob job, CancellationToken ct);

    /// <summary>
    /// Нужно ли удерживать per-repo лок на время выполнения (строго одна джоба репозитория).
    /// True только для джоб, мутирующих git-репозиторий (clone/fetch — ref-lock race).
    /// Чистые читатели/DB-джобы (Index/Embedding/Relations/Rebuild) идут параллельно в рамках репо.
    /// </summary>
    bool BlocksRepo => true;
}
