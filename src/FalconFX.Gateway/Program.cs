using FalconFX.Gateway.Hubs;
using FalconFX.Gateway.Workers;
using FalconFX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// 1. Add Redis (Used for both our Worker AND SignalR Backplane)
// Best Practice: If you ever scale to 2 Gateway instances, this ensures 
// a user connected to Gateway A gets messages from Gateway B.
builder.AddRedisClient("redis");

// 2. Add SignalR with Redis Backplane
// We must manually get the connection string because AddStackExchangeRedis 
// doesn't automatically look up Aspire resources by default like AddRedisClient does.
var redisConnectionString = builder.Configuration.GetConnectionString("redis");

builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString!, options =>
    {
        // Optional: Prefix to distinguish SignalR internal channels from your market data
        options.Configuration.ChannelPrefix = "FalconFX.SignalR";
    });

// 2. Register Background Worker (Redis Listener)
builder.Services.AddHostedService<RedisSubscriber>();

// 3. CORS (Only needed if you develop frontend externally, e.g. React server)
// Since we are serving static files, strict CORS is fine.
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

// 4. 🔥 Enable Static Files (Serves index.html from wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowAll");

// 5. Map Hub
app.MapHub<MarketHub>("/markethub");

// 6. Fallback for SPA (Single Page Application) routing
app.MapFallbackToFile("index.html");

app.Run();