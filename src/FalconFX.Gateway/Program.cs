using System.Text;
using FalconFX.Gateway.Hubs;
using FalconFX.Gateway.Workers;
using FalconFX.ServiceDefaults;
using StackExchange.Redis;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// 1. Add Redis Client
var redisConnectionString = builder.Configuration.GetConnectionString("redis");
if (string.IsNullOrWhiteSpace(redisConnectionString))
    throw new InvalidOperationException("Redis connection string 'redis' is missing.");

builder.AddRedisClient("redis");

// 2. Configure SignalR with Redis Backplane for Horizontal Scaling
builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("FalconFX.SignalR");
    });

// 3. Register Background Worker (Redis Listener)
builder.Services.AddHostedService<RedisSubscriber>();

// 4. CORS Policy for Frontend / React / Web Clients
// Never combine AllowCredentials() with a wildcard/reflected origin: that enables
// Cross-Site WebSocket Hijacking (CSWSH) against the SignalR hub.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("GatewayCors", policy =>
    {
        if (allowedOrigins is { Length: > 0 })
            policy.WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        else if (builder.Environment.IsDevelopment())
            policy.WithOrigins("http://localhost:5224", "https://localhost:7139")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        else
            throw new InvalidOperationException(
                "Cors:AllowedOrigins must be configured in non-development environments.");
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Enable Static Files (Serves index.html from wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("GatewayCors");

// Map SignalR Hub
app.MapHub<MarketHub>("/markethub");

// Fallback for SPA (Single Page Application)
app.MapFallbackToFile("index.html");

await app.RunAsync();
