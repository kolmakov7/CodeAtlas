using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class IndexingErrorStore : IIndexingErrorStore
{
    private readonly IDbConnectionFactory _factory;

    public IndexingErrorStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(Guid repositoryId, string filePath, string message, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"INSERT INTO indexing_errors(id, repository_id, file_path, message, at)
              VALUES (@id, @repo, @path, @msg, @at);",
            new
            {
                id = Guid.NewGuid().ToString(),
                repo = repositoryId.ToString(),
                path = filePath,
                msg = message.Length <= 2000 ? message : message[..2000],
                at = DateTimeOffset.UtcNow.ToString("o")
            }).ConfigureAwait(false);
    }
}
