using System.Net;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// Requires Aspire.Hosting.Testing package
// Auto-generated namespace from AppHost reference

namespace FalconFX.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task AppHost_Should_Start_And_Gateway_Should_Be_Healthy()
    {
        // 1. Arrange: Boot up the entire Distributed Application
        // This starts containers for Redis, Kafka, Postgres and your .NET projects
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<AppHostProgram>();

        // Optional: Override configuration for tests (e.g. smaller buffers)
        appHost.Services.ConfigureHttpClientDefaults(client =>
        {
            // Allow self-signed certs for testing if needed
        });

        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        // 2. Act: Wait for the Gateway to be ready
        // Aspire handles service discovery translation (http://gateway -> localhost:port)
        var httpClient = app.CreateHttpClient("gateway");

        // Polling for health (Containers take time to start)
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // Give Kafka time to warm up
        HttpResponseMessage response = null!;

        while (!cts.IsCancellationRequested)
            try
            {
                response = await httpClient.GetAsync("/health", cts.Token);
                if (response.StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException)
            {
                // Service might not be listening yet
                await Task.Delay(1000);
            }

        // 3. Assert
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}