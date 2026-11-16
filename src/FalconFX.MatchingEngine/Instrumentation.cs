using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FalconFX.MatchingEngine;

public static class Instrumentation
{
    public const string ServiceName = "FalconFX.MatchingEngine";

    // OpenTelemetry Meter
    private static readonly Meter Meter = new(ServiceName);

    // Definition for Traces (Timelines)
    public static readonly ActivitySource ActivitySource = new(ServiceName);

    /// <summary>
    ///     Observes counter metrics on-demand without overhead in the matching hot path.
    ///     The Aspire dashboard / OTLP exporter polls these values periodically.
    /// </summary>
    public static void RegisterObservableMetrics(
        Func<long> getOrdersProcessed,
        Func<long> getTradesCreated,
        Func<long> getRiskRejected,
        Func<long> getBookRejected)
    {
        Meter.CreateObservableCounter(
            "orders_processed",
            getOrdersProcessed,
            "{orders}",
            "Total orders processed by the matching engine");

        Meter.CreateObservableCounter(
            "trades_created",
            getTradesCreated,
            "{trades}",
            "Total trades matched by the engine");

        Meter.CreateObservableCounter(
            "orders_rejected_risk",
            getRiskRejected,
            "{orders}",
            "Total orders rejected by pre-trade risk checks");

        Meter.CreateObservableCounter(
            "orders_rejected_book",
            getBookRejected,
            "{orders}",
            "Total orders rejected by the order book");
    }
}
