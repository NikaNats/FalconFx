using Microsoft.Extensions.Logging;

namespace FalconFX.ServiceDefaults;

/// <summary>
/// High-Performance, Zero-Allocation Structured Logging for FalconFX Services.
/// Uses C# Source Generators to eliminate string interpolation, boxing, and allocations.
/// </summary>
public static partial class LoggingExtensions
{
    // ── Service Lifecycle ──

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "🚀 {ServiceName} starting...")]
    public static partial void LogServiceStarting(
        this ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "⏳ Waiting for Topic Leader Election...")]
    public static partial void LogWaitingLeaderElection(
        this ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "🚀 Producer started. Entering hot loop.")]
    public static partial void LogProducerStarted(
        this ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "🛑 {ServiceName} stopped gracefully.")]
    public static partial void LogServiceStopped(
        this ILogger logger, string serviceName);

    // ── Hot Path Progress ──

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "🔥 Sent {Count:N0} orders")]
    public static partial void LogOrdersSent(
        this ILogger logger, long count);

    // ── Errors & Warnings ──

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Produce queue overflow or transient error: {Reason}. Retrying...")]
    public static partial void LogProduceError(
        this ILogger logger, string reason);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Kafka Connection string 'kafka' is missing or invalid.")]
    public static partial void LogMissingConnectionString(
        this ILogger logger);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Critical,
        Message = "Fatal unhandled exception in Worker loop.")]
    public static partial void LogFatalError(
        this ILogger logger, Exception ex);

    // ── Shutdown / Flush ──

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "🧹 Flushing Kafka Producer in-flight messages...")]
    public static partial void LogFlushingProducer(
        this ILogger logger);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Failed to flush Kafka producer gracefully: {Reason}")]
    public static partial void LogFlushError(
        this ILogger logger, string reason);
}