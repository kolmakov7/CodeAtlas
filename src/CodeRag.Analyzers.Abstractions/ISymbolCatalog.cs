namespace CodeRag.Analyzers.Abstractions;

/// <summary>Символ из персистентного каталога репозитория.</summary>
public sealed record CatalogSymbol(Guid Id, string? Fqn, string Name);

/// <summary>
/// Каталог уже сохранённых символов репозитория.
/// Нужен экстракторам связей, чтобы сопоставить извлечённые FQN с реальными ID символов в БД.
/// </summary>
public interface ISymbolCatalog
{
    Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default);
}
