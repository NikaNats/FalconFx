using System.Text;
using Confluent.Kafka;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor;
using FalconFX.TradeProcessor.Data;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// 1. Configure Kafka Consumer for Trade Ingestion
builder.AddKafkaConsumer<Null, byte[]>("kafka", settings =>
{
    settings.Config.GroupId = "trade-processor-group";
    settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
    settings.Config.EnableAutoCommit = false;
});

// 2. Configure PostgreSQL DbContext with GSSAPI Negotiation Disabled
builder.AddNpgsqlDbContext<TradeDbContext>("trade-db", settings =>
{
    var rawConn = builder.Configuration.GetConnectionString("trade-db");
    if (!string.IsNullOrEmpty(rawConn))
    {
        var csBuilder = new NpgsqlConnectionStringBuilder(rawConn)
        {
            // Disable GSSAPI Kerberos negotiation to prevent PostgreSQL authentication warnings
            GssEncryptionMode = GssEncryptionMode.Disable
        };
        settings.ConnectionString = csBuilder.ConnectionString;
    }
});

// 3. Register Redis Client for Real-Time Market Ticker Broadcasting
builder.AddRedisClient("redis");

// 4. Register TradeProcessor Background Worker Service
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();