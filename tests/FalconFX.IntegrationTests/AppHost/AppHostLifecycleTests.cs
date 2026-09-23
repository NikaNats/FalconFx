using System.Net;
using Aspire.Hosting.Testing;
using FalconFX.AppHost;
using FluentAssertions;

namespace FalconFX.IntegrationTests.AppHost;

public class AppHostLifecycleTests
{
    [Fact]
    public async Task AppHost_ShouldStartSuccessfully_AndGatewayHealthEndpointShouldReturnOK()
    {
        // Arrange: Aspire AppHost-ის ორკესტრაციის ჩატვირთვა
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<AppHostProgram>(cancellationToken);

        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        // Act: Gateway სერვისის HTTP კლიენტის შექმნა
        var httpClient = app.CreateHttpClient("gateway");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // კონტეინერების გაშვების დრო
        HttpResponseMessage? response = null;

        while (!cts.IsCancellationRequested)
            try
            {
                response = await httpClient.GetAsync("/health", cts.Token);
                if (response.StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1000, cts.Token);
            }

        // Assert
        response.Should().NotBeNull();
        response!.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}