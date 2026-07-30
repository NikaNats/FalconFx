using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Grpc.Core;

namespace FalconFX.MatchingEngine.Services;

public sealed class GrpcOrderService(
    EngineWorker engine,
    ILogger<GrpcOrderService> logger) : OrderService.OrderServiceBase
{
    public override async Task<SubmitOrderResponse> StreamOrders(
        IAsyncStreamReader<SubmitOrderRequest> requestStream,
        ServerCallContext context)
    {
        try
        {
            // BEST PRACTICE: Pass CancellationToken to MoveNext
            // This stops the loop immediately if the connection is cut.
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var req = requestStream.Current;

                // Zero-alloc struct mapping
                var order = new Order(
                    req.Id,
                    (OrderSide)req.Side,
                    req.Price,
                    req.Quantity
                );

                engine.EnqueueOrder(order);
            }
        }
        catch (IOException)
        {
            // Expected behavior when client disconnects (RST_STREAM)
            logger.LogInformation("Client disconnected stream.");
        }
        catch (OperationCanceledException)
        {
            // Expected when server shuts down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in order stream");
        }

        return new SubmitOrderResponse { Success = true, Message = "Session Ended" };
    }
}