using System.Text;
using CodeRag.Domain.Ports;
using CodeRag.Search.Models;
using Microsoft.Extensions.Options;

namespace CodeRag.Search.Services;

public sealed class FileContentService
{
    private readonly IRepositoryStore _repos;
    private readonly IFileStore _files;
    private readonly IChunkStore _chunks;
    private readonly IOptionsMonitor<SearchOptions> _opts;
    public FileContentService(IRepositoryStore repos, IFileStore files, IChunkStore chunks, IOptionsMonitor<SearchOptions> opts)
    {
        _repos = repos;
        _files = files;
        _chunks = chunks;
        _opts = opts;
    }

    public async Task<SourceSlice> GetAsync(GetCodeRequest req, CancellationToken ct)
    {
        if (req.RepositoryId == Guid.Empty) throw new ArgumentException("RepositoryId must not be empty.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Path)) throw new ArgumentException("Path must not be empty.", nameof(req));
        var requestedPath = req.Path.Trim();
        if (requestedPath.Contains('\0')) throw new ArgumentException("Path contains invalid characters.", nameof(req));
        var normalizedReq = requestedPath.Replace('\\', '/').TrimStart('/');
        if (normalizedReq.Contains("..")) throw new ArgumentException("Path traversal not allowed: '..' in path.", nameof(req));
        if (Path.IsPathRooted(requestedPath)) throw new ArgumentException("Absolute path not allowed: use path relative to repository root.", nameof(req));
        var localPath = await _repos.GetLocalPathAsync(req.RepositoryId, ct).ConfigureAwait(false);
        if (localPath is null) throw new InvalidOperationException($"Repository not found: {req.RepositoryId}");
        if (string.IsNullOrWhiteSpace(localPath)) throw new InvalidOperationException("Repository local_path is empty.");
        var normalizedLocal = Path.GetFullPath(localPath);
        var fileRow = await _files.GetByPathAsync(req.RepositoryId, normalizedReq, ct).ConfigureAwait(false);
        if (fileRow is null) fileRow = await _files.GetByPathAsync(req.RepositoryId, requestedPath, ct).ConfigureAwait(false);
        if (fileRow is null) throw new InvalidOperationException($"File not indexed: repository={req.RepositoryId} path='{normalizedReq}'");
        var fileId = fileRow.Id;
        var language = fileRow.Language ?? "unknown";
        var combined = Path.Combine(normalizedLocal, fileRow.Path.Replace('/', Path.DirectorySeparatorChar));
        var fullFilePath = Path.GetFullPath(combined);
        if (!fullFilePath.StartsWith(normalizedLocal, StringComparison.Ordinal)) throw new InvalidOperationException("Path traversal detected: resolved path outside repository.");
        string[] lines;
        if (File.Exists(fullFilePath))
        {
            var text = await File.ReadAllTextAsync(fullFilePath, ct).ConfigureAwait(false);
            lines = text.Split('\n');
        }
        else
        {
            var chunks = await _chunks.GetByFileAsync(fileId, ct).ConfigureAwait(false);
            if (chunks.Count == 0) throw new InvalidOperationException($"File not found on disk and no chunks available: {fullFilePath}");
            var sb = new StringBuilder();
            foreach (var c in chunks) sb.AppendLine(c.Source);
            lines = sb.ToString().Split('\n');
        }
        for (int i = 0; i < lines.Length; i++) if (lines[i].EndsWith("\r")) lines[i] = lines[i][..^1];
        int totalLines = lines.Length;
        int start = req.StartLine.HasValue ? Math.Clamp(req.StartLine.Value, 1, totalLines) : 1;
        int end = req.EndLine.HasValue ? Math.Clamp(req.EndLine.Value, start, totalLines) : totalLines;
        int maxLines = req.MaxLines.HasValue ? Math.Clamp(req.MaxLines.Value, 1, 1000) : _opts.CurrentValue.PreviewMaxLines;
        if (end - start + 1 > maxLines) end = start + maxLines - 1;
        var sliceLines = lines.Skip(start - 1).Take(end - start + 1).ToArray();
        var content = string.Join("\n", sliceLines);
        var previewLines = sliceLines.Length > _opts.CurrentValue.PreviewMaxLines ? sliceLines.Take(_opts.CurrentValue.PreviewMaxLines).ToArray() : sliceLines;
        var preview = string.Join("\n", previewLines);
        return new SourceSlice(req.RepositoryId, fileId, fileRow.Path, language, start, end, totalLines, content, preview);
    }
}
