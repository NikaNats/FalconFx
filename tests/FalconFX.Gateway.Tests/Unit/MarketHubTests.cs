using FalconFX.Gateway.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FalconFX.Gateway.Tests.Unit;

public class MarketHubTests
{
    [Fact]
    public async Task OnConnectedAsync_ShouldLogAndCompleteSuccessfully()
    {
        // Arrange
        var logger = NullLogger<MarketHub>.Instance;
        var hub = new MarketHub(logger);

        var hubCallerContext = Substitute.For<HubCallerContext>();
        hubCallerContext.ConnectionId.Returns("CONN_ID_TEST_123");
        hub.Context = hubCallerContext;

        // Act
        var act = async () => await hub.OnConnectedAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnDisconnectedAsync_ShouldLogAndCompleteSuccessfully()
    {
        // Arrange
        var logger = NullLogger<MarketHub>.Instance;
        var hub = new MarketHub(logger);

        var hubCallerContext = Substitute.For<HubCallerContext>();
        hubCallerContext.ConnectionId.Returns("CONN_ID_TEST_123");
        hub.Context = hubCallerContext;

        // Act
        var act = async () => await hub.OnDisconnectedAsync(new Exception("Connection Lost"));

        // Assert
        await act.Should().NotThrowAsync();
    }
}