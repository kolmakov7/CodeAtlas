using CodeRag.Application.UseCases;
using CodeRag.Domain.Ports;
using FluentAssertions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

public sealed class GetActivityQueryHandlerTests
{
    [Fact]
    public async Task Handle_should_clamp_limit_and_delegate()
    {
        var repo = Substitute.For<IActivityRepository>();
        repo.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ActivityDto> { new("id1", DateTimeOffset.UtcNow.ToString("o"), "actor", "action", null, null) });

        var sut = new GetActivityQueryHandler(repo);
        var result = await sut.HandleAsync(200);
        await repo.Received(1).ListAsync(100, Arg.Any<CancellationToken>());
        result.Should().HaveCount(1);

        await sut.HandleAsync(0);
        await repo.Received(1).ListAsync(1, Arg.Any<CancellationToken>());

        await sut.HandleAsync(20);
        await repo.Received(1).ListAsync(20, Arg.Any<CancellationToken>());
    }
}
