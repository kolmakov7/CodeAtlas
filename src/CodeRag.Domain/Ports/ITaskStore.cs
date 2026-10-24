using CodeRag.Domain.Entities;
using CodeRag.Domain.Models;

namespace CodeRag.Domain.Ports;

public interface ITaskStore
{
    Task CreateAsync(TaskItem task, CancellationToken ct = default);
    Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TaskItem?> GetByExternalKeyAsync(Guid? projectId, string externalKey, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItem>> ListAsync(TaskQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItem>> GetChildrenAsync(Guid parentTaskId, bool includeArchived, CancellationToken ct = default);
    Task<bool> UpdateAsync(TaskItem task, int expectedVersion, CancellationToken ct = default);
    Task AddCommentAsync(TaskComment comment, CancellationToken ct = default);
    Task<IReadOnlyList<TaskComment>> GetCommentsAsync(Guid taskId, int limit = 100, CancellationToken ct = default);
    Task<int> CountByProjectAsync(Guid? projectId, CancellationToken ct = default);
}
