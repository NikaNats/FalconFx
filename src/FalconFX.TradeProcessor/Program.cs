using Confluent.Kafka;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor;
using FalconFX.TradeProcessor.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Kafka Consumer
builder.AddKafkaConsumer<Null, byte[]>("kafka", settings =>
{
    settings.Config.GroupId = "trade-processor-group";
    settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
    settings.Config.EnableAutoCommit = false;
});

// Postgres Context
builder.AddNpgsqlDbContext<TradeDbContext>("trade-db");

// Redis Client
builder.AddRedisClient("redis");

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();