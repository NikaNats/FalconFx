using FalconFX.Gateway.Hubs;
using FalconFX.Gateway.Workers;
using FalconFX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// 1. Add Redis Client
builder.AddRedisClient("redis");

// 2. Configure SignalR with Redis Backplane for Horizontal Scaling
var redisConnectionString = builder.Configuration.GetConnectionString("redis");

builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString!,
        options => { options.Configuration.ChannelPrefix = "FalconFX.SignalR"; });

// 3. Register Background Worker (Redis Listener)
builder.Services.AddHostedService<RedisSubscriber>();

// 4. CORS Policy for Frontend / React / Web Clients
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Enable Static Files (Serves index.html from wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowAll");

// Map SignalR Hub
app.MapHub<MarketHub>("/markethub");

// Fallback for SPA (Single Page Application)
app.MapFallbackToFile("index.html");

await app.RunAsync();