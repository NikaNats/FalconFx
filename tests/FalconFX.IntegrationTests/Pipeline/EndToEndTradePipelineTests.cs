using System.Net;
using Aspire.Hosting.Testing;
using FalconFX.AppHost;
using FluentAssertions;
using Xunit;

namespace FalconFX.IntegrationTests.Pipeline;

public class EndToEndTradePipelineTests
{
    [Fact]
    public async Task Gateway_ShouldServeWebIndexHtml_AndExposeMarketHubEndpoint()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<AppHostProgram>();

        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        var httpClient = app.CreateHttpClient("gateway");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act 1: Get Index Page
        var indexResponse = await httpClient.GetAsync("/", cts.Token);

        // Assert 1
        indexResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await indexResponse.Content.ReadAsStringAsync(cts.Token);
        content.Should().Contain("FALCON");

        // Act 2: Check SignalR Hub endpoint availability
        var hubNegotiateResponse = await httpClient.PostAsync("/markethub/negotiate?negotiateVersion=1", null, cts.Token);

        // Assert 2: SignalR Negotiate should return 200 OK or 400 (if version missing), but not 404
        hubNegotiateResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}