using CodeRag.Api.Services;
using CodeRag.Domain.Ports;
using FluentAssertions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task GetAll_delegates_to_repo()
    {
        var repo = Substitute.For<ISettingsRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string> { ["k"] = "v" });
        var sut = new SettingsService(repo);
        var result = await sut.GetAllAsync();
        result["k"].Should().Be("v");
        await repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_delegates()
    {
        var repo = Substitute.For<ISettingsRepository>();
        repo.UpsertAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["a"] = "b" });
        var sut = new SettingsService(repo);
        var res = await sut.UpsertAsync(new Dictionary<string, string> { ["a"] = "b" });
        res["a"].Should().Be("b");
    }
}
