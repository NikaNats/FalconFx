using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FalconFX.MatchingEngine;

public static class Instrumentation
{
    // Matches the Project Name for auto-discovery
    public const string ServiceName = "FalconFX.MatchingEngine";

    // 1. Definition for Metrics (Graphs)
    private static readonly Meter Meter = new(ServiceName);

    public static readonly Counter<long> OrdersProcessed =
        Meter.CreateCounter<long>("orders_processed", description: "Total orders processed");

    public static readonly Counter<long> TradesCreated =
        Meter.CreateCounter<long>("trades_created", description: "Total trades matched");

    // 2. Definition for Traces (Timelines)
    public static readonly ActivitySource ActivitySource = new(ServiceName);
}