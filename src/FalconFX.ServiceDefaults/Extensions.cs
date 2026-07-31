using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FalconFX.ServiceDefaults;

/// <summary>
///     Shared ServiceDefaults extensions providing OpenTelemetry instrumentation,
///     health check endpoints, service discovery, and resilient HTTP client defaults.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    ///     Maps default health check and liveness endpoints for HTTP web applications.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);

            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }

    /// <summary>
    ///     Configures core service defaults including OpenTelemetry, health checks, and service discovery.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http => { http.AddServiceDiscovery(); });

        return builder;
    }

    /// <summary>
    ///     Configures OpenTelemetry metrics, tracing, and logging instrumentation.
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                // Register MatchingEngine custom Meter for OTel metrics aggregation
                metrics.AddMeter("MatchingEngine");
            })
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsDevelopment())
                    tracing.SetSampler(new AlwaysOnSampler());

                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("MatchingEngine")
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Exclude health check endpoints from distributed traces to minimize span noise
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath) &&
                            !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);
                    })
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());

            builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
            {
                metrics.AddOtlpExporter((otlpOptions, readerOptions) =>
                {
                    // 5-second periodic export interval prevents telemetry flooding in HFT loops
                    readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000;
                });
            });

            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
        }

        return builder;
    }

    /// <summary>
    ///     Registers default health checks and configures publisher options.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        // Configure health check publisher options for fast teardown probes
        builder.Services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Period = TimeSpan.FromSeconds(15);
            options.Timeout = TimeSpan.FromSeconds(2);
        });

        return builder;
    }
}