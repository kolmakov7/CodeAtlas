using CodeRag.Domain.Ports;

namespace CodeRag.Infrastructure.Stores;

/// <summary>Legacy façade — объявляется в Domain.Ports, здесь прокси для совместимости.</summary>
public interface IRepositoryStore : CodeRag.Domain.Ports.IRepositoryStore { }
