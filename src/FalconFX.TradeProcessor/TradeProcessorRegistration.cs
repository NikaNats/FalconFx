using Confluent.Kafka;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor.Data;
using Npgsql;

namespace FalconFX.TradeProcessor;

/// <summary>
///     Registers the TradeProcessor service graph. Kept separate from top-level
///     <c>Program</c> so the DI composition (in particular the <see cref="NpgsqlDataSource" />
///     required by the binary COPY path) can be asserted by tests without starting the host.
/// </summary>
internal static class TradeProcessorRegistration
{
    internal static IHostApplicationBuilder AddTradeProcessorServices(this IHostApplicationBuilder builder)
    {
        builder.AddServiceDefaults();

        // 1. Configure High-Throughput Kafka Consumer
        builder.AddKafkaConsumer<Ignore, byte[]>("kafka", settings =>
        {
            settings.Config.GroupId = "trade-processor-group";
            settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
            settings.Config.EnableAutoCommit = true;
            settings.Config.AutoCommitIntervalMs = 1000;
            settings.Config.EnableAutoOffsetStore = false;
            settings.Config.FetchMinBytes = 1; // Zero fetch delay
            settings.Config.FetchMaxBytes = 10_000_000; // 10MB batch capacity
            settings.Config.MaxPollIntervalMs = 300000;
            settings.Config.SocketTimeoutMs = 30000;
        });

        // 2. Configure PostgreSQL
        var tradeDbConnectionString = builder.Configuration.GetConnectionString("trade-db")
                                      ?? throw new InvalidOperationException(
                                          "PostgreSQL connection string 'trade-db' is missing.");

        // Local Aspire containers do not offer TLS/GSSAPI; disabling them is only safe in Development.
        if (builder.Environment.IsDevelopment())
        {
            var csBuilder = new NpgsqlConnectionStringBuilder(tradeDbConnectionString)
            {
                GssEncryptionMode = GssEncryptionMode.Disable,
                SslMode = SslMode.Disable
            };
            tradeDbConnectionString = csBuilder.ConnectionString;
        }

        builder.AddNpgsqlDbContext<TradeDbContext>("trade-db", settings =>
        {
            settings.ConnectionString = tradeDbConnectionString;
        });

        // AddNpgsqlDbContext only registers the DbContext. The binary COPY hot path needs the
        // underlying data source, so register it explicitly against the same connection string.
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(tradeDbConnectionString));

        builder.AddRedisClient("redis");
        builder.Services.AddHostedService<Worker>();

        return builder;
    }
}
