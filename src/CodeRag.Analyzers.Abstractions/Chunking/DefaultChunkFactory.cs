using System.Security.Cryptography;
using System.Text;

namespace CodeRag.Analyzers.Abstractions.Chunking;

public sealed class DefaultChunkFactory : IChunkFactory
{
    public string ComputeHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Chunk Create(Guid? symbolId, string language, int startLine, int endLine, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("source must not be empty", nameof(source));
        var hash = ComputeHash(source);
        return new Chunk(Guid.NewGuid(), symbolId, language, startLine, endLine, source, hash);
    }
}
