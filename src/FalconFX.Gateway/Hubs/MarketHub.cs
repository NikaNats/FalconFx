using Microsoft.AspNetCore.SignalR;

namespace FalconFX.Gateway.Hubs;

// Strongly typed interface prevents "Magic Strings" in the SendAsync call
public interface IMarketClient
{
    Task ReceiveMarketUpdate(string symbol, long price);
}

public sealed class MarketHub : Hub<IMarketClient>
{
    private readonly ILogger<MarketHub> _logger;

    public MarketHub(ILogger<MarketHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        _logger.LogClientConnected(Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogClientDisconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

// ═══════════════════════════════════════════
//  ZERO-ALLOCATION LOGGING EXTENSIONS
// ═══════════════════════════════════════════
internal static partial class MarketHubLogExtensions
{
    [LoggerMessage(EventId = 401, Level = LogLevel.Information, Message = "Client Connected: {ConnectionId}")]
    public static partial void LogClientConnected(this ILogger logger, string connectionId);

    [LoggerMessage(EventId = 402, Level = LogLevel.Information, Message = "Client Disconnected: {ConnectionId}")]
    public static partial void LogClientDisconnected(this ILogger logger, string connectionId);
}