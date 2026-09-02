namespace CodeRag.Analyzers.Abstractions.Chunking;

/// <summary>
/// Единственная точка создания <see cref="Chunk"/> + хеширования.
/// Общая для всех анализаторов — язык-агностична.
/// </summary>
public interface IChunkFactory
{
    string ComputeHash(string source);
    Chunk Create(Guid? symbolId, string language, int startLine, int endLine, string source);
}
