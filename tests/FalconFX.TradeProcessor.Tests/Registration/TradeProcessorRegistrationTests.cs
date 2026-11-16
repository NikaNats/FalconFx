using FalconFX.TradeProcessor;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace FalconFX.TradeProcessor.Tests.Registration;

public class TradeProcessorRegistrationTests
{
    [Fact]
    public void AddTradeProcessorServices_RegistersNpgsqlDataSource_ForBinaryCopyPath()
    {
        // AddNpgsqlDbContext only registers the DbContext; Worker resolves NpgsqlDataSource
        // directly for COPY FROM STDIN, so the registration must exist independently.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trade-db"] =
                "Host=127.0.0.1;Port=5432;Database=trade-db;Username=postgres;Password=postgres",
            ["ConnectionStrings:kafka"] = "127.0.0.1:9092",
            ["ConnectionStrings:redis"] = "127.0.0.1:6379"
        });

        builder.AddTradeProcessorServices();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<NpgsqlDataSource>().Should().NotBeNull();
    }
}
