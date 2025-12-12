using Microsoft.AspNetCore.SignalR;

namespace FalconFX.Gateway.Hubs;

// Strongly typed interface prevents "Magic Strings" in the SendAsync call
public interface IMarketClient
{
    Task ReceiveMarketUpdate(string symbol, long price);
}

public class MarketHub(ILogger<MarketHub> logger) : Hub<IMarketClient>
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        logger.LogInformation("Client Connected: {ContextConnectionId}", Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client Disconnected: {ContextConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}