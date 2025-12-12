using Confluent.Kafka;
using FalconFX.ServiceDefaults;
using TradeProcessor;
using TradeProcessor.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Kafka Consumer
builder.AddKafkaConsumer<string, byte[]>("kafka", settings =>
{
    settings.Config.GroupId = "trade-processor-group";
    settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
});

// Postgres
builder.AddNpgsqlDbContext<TradeDbContext>("trade-db");

// Redis
builder.AddRedisClient("redis");

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();