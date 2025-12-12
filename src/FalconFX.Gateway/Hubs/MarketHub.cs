using Microsoft.AspNetCore.SignalR;

namespace FalconFX.Gateway.Hubs;

// Strongly typed interface prevents "Magic Strings" in the SendAsync call
public interface IMarketClient
{
    Task ReceiveMarketUpdate(string symbol, long price);
}

public class MarketHub : Hub<IMarketClient>
{
    private readonly ILogger<MarketHub> _logger;

    public MarketHub(ILogger<MarketHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        _logger.LogInformation($"Client Connected: {Context.ConnectionId}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client Disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }
}