using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using FluentAssertions;

namespace CodeRag.Tests.Domain;

/// <summary>
/// Регрессия: скорость/ETA джобы (страница Jobs) считаются от момента фактического
/// старта выполнения, а не от момента постановки в очередь. Иначе в знаменатель
/// попадает время ожидания в очереди / слоте семафора / per-repo локе, и скорость
/// обработанных embeddings получается заниженной, а ETA — завышенным.
/// </summary>
public sealed class IndexingJobTimingTests
{
    private static IndexingJob CreateQueued(DateTimeOffset at) =>
        IndexingJob.Create(Guid.NewGuid(), Guid.NewGuid(), JobType.Embedding, JobStatus.Queued, at);

    [Fact]
    public void Create_sets_StartedAt_to_enqueue_time()
    {
        var queuedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var job = CreateQueued(queuedAt);

        job.Status.Should().Be(JobStatus.Queued);
        job.StartedAt.Should().Be(queuedAt, "started_at фиксируется в момент постановки в очередь");
    }

    [Fact]
    public void MarkRunning_moves_StartedAt_to_actual_run_start()
    {
        var queuedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var ranAt = queuedAt.AddMinutes(30); // 30 минут простоя в очереди / ожидании слота

        var job = CreateQueued(queuedAt);
        job.MarkRunning(ranAt);

        job.Status.Should().Be(JobStatus.Running);
        job.StartedAt.Should().Be(ranAt,
            "статистика скорости должна считаться от старта выполнения, без времени ожидания в очереди");
    }

    [Fact]
    public void Speed_denominator_excludes_queue_wait()
    {
        var queuedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var ranAt = queuedAt.AddHours(2); // долгое ожидание

        var job = CreateQueued(queuedAt);
        job.MarkRunning(ranAt);
        job.SetProgress(0.5, totalItems: 1000, itemsDone: 500, phase: "embedding");

        // Через 60 секунд фактической работы над embeddings.
        var now = ranAt.AddSeconds(60);
        var elapsedSec = Math.Max(1, (now - job.StartedAt).TotalSeconds);
        var speed = job.ItemsDone!.Value / elapsedSec;

        elapsedSec.Should().Be(60, "в знаменателе только реальное время работы");
        speed.Should().BeApproximately(500d / 60d, 1e-9);

        // Старое (баговое) поведение давало бы ~0.07 items/s вместо ~8.3 items/s.
        var queueInclusiveSpeed = job.ItemsDone!.Value / (now - queuedAt).TotalSeconds;
        speed.Should().BeGreaterThan(queueInclusiveSpeed * 100);
    }

    [Fact]
    public void MarkRunning_without_timestamp_sets_StartedAt_to_now()
    {
        var job = CreateQueued(DateTimeOffset.UtcNow.AddMinutes(-5));
        var before = DateTimeOffset.UtcNow;

        job.MarkRunning();

        job.StartedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void MarkRunning_is_only_allowed_from_Queued()
    {
        var job = IndexingJob.Create(Guid.NewGuid(), Guid.NewGuid(), JobType.Embedding);
        job.MarkRunning();

        var act = () => job.MarkRunning();

        act.Should().Throw<DomainValidationException>();
    }
}
