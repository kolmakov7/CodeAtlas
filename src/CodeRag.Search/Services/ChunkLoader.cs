using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;

namespace CodeRag.Search.Services;

public sealed class ChunkLoader
{
    private readonly IChunkStore _store;
    public ChunkLoader(IChunkStore store) => _store = store;

    public async Task<Dictionary<Guid, ChunkInfo>> LoadAsync(List<Guid> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return new Dictionary<Guid, ChunkInfo>();
        var rows = await _store.GetSearchInfoByIdsAsync(chunkIds, ct).ConfigureAwait(false);
        var dict = new Dictionary<Guid, ChunkInfo>(rows.Count);
        foreach (var r in rows) dict[r.ChunkId] = r;
        return dict;
    }
}
