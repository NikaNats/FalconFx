using FalconFX.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

// 🔥 ASPIRE 13 INTEGRATION
// This wires up OpenTelemetry, Metrics, and Health Checks automatically
builder.AddServiceDefaults();

// Register services here
// builder.Services.AddSingleton<...>();

var host = builder.Build();

await host.RunAsync();