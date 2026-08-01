using System.Text;
using Confluent.Kafka;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor;
using FalconFX.TradeProcessor.Data;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

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

// 2. Configure PostgreSQL (Disable GSSAPI & SSL on local connection string)
builder.AddNpgsqlDbContext<TradeDbContext>("trade-db", settings =>
{
    var rawConn = builder.Configuration.GetConnectionString("trade-db");
    if (!string.IsNullOrEmpty(rawConn))
    {
        var csBuilder = new NpgsqlConnectionStringBuilder(rawConn)
        {
            GssEncryptionMode = GssEncryptionMode.Disable,
            SslMode = SslMode.Disable
        };
        settings.ConnectionString = csBuilder.ConnectionString;
    }
});

builder.AddRedisClient("redis");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();