using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using MarketMaker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHostedService<Worker>();

// BEST PRACTICE: 
// 1. Do NOT use AddResilienceHandler() or AddStandardResilienceHandler() for Streaming clients.
//    Polly timeouts will kill the stream, causing the "Reset Stream" error.
// 2. We configure the HttpClient directly for long-lived connections.
builder.Services
    .AddGrpcClient<OrderService.OrderServiceClient>(o => { o.Address = new Uri("https://matching-engine"); })
// CRITICAL FIX: Use ConfigurePrimaryHttpMessageHandler
// This allows Service Discovery to wrap around this handler.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
        EnableMultipleHttp2Connections = true
    });

var host = builder.Build();
host.Run();