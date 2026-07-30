using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FalconFX.MatchingEngine;

public static class Instrumentation
{
    public const string ServiceName = "FalconFX.MatchingEngine";

    // OpenTelemetry Meter
    private static readonly Meter Meter = new(ServiceName);

    // 2. Definition for Traces (Timelines)
    public static readonly ActivitySource ActivitySource = new(ServiceName);

    /// <summary>
    /// Observes counter metrics on-demand without overhead in the matching hot path.
    /// Prometheus/Aspire will poll these values periodically.
    /// </summary>
    public static void RegisterObservableMetrics(Func<long> getOrdersProcessed, Func<long> getTradesCreated)
    {
        Meter.CreateObservableCounter(
            "orders_processed",
            getOrdersProcessed,
            unit: "{orders}",
            description: "Total orders processed by the matching engine");

        Meter.CreateObservableCounter(
            "trades_created",
            getTradesCreated,
            unit: "{trades}",
            description: "Total trades matched by the engine");
    }
}