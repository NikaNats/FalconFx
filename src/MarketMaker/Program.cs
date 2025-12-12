using FalconFX.ServiceDefaults;
using MarketMaker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();