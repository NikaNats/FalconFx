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
// Keeping in this namespace eases discovery

public static class Extensions
{
    // Constants for Health Check routes
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    extension<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        public TBuilder AddServiceDefaults()
        {
            builder.ConfigureOpenTelemetry();

            builder.AddDefaultHealthChecks();

            builder.Services.AddServiceDiscovery();

            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                // Turn on resilience by default (Retry, Circuit Breaker, etc.)
                http.AddStandardResilienceHandler();

                // Turn on service discovery by default
                http.AddServiceDiscovery();
            });

            return builder;
        }

        public TBuilder ConfigureOpenTelemetry()
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

                    // IMPORTANT for MatchingEngine: 
                    // We add the custom Meter name here so your custom metrics work automatically.
                    metrics.AddMeter("MatchingEngine");
                })
                .WithTracing(tracing =>
                {
                    if (builder.Environment.IsDevelopment())
                        // View all traces in dev
                        tracing.SetSampler(new AlwaysOnSampler());

                    tracing.AddSource(builder.Environment.ApplicationName)
                        // Add MatchingEngine Source for custom activities
                        .AddSource("MatchingEngine")
                        .AddAspNetCoreInstrumentation(options =>
                        {
                            // Exclude health check requests from tracing to reduce noise
                            options.Filter = context =>
                                !context.Request.Path.StartsWithSegments(HealthEndpointPath) &&
                                !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);
                        })
                        .AddHttpClientInstrumentation();
                });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        private TBuilder AddOpenTelemetryExporters()
        {
            var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                // We use the advanced configuration to allow us to tweak the export interval
                // which is critical for your HFT engine performance (Batching).
                builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());

                builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
                {
                    metrics.AddOtlpExporter((otlpOptions, readerOptions) =>
                    {
                        // Standard config uses Grpc by default if endpoint is set
                        // We increase the export interval to 5 seconds (default is usually 60s or 1s depending on config)
                        // to prevent flooding the Aspire Dashboard with updates from your tight loop.
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000;
                    });
                });

                builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
            }

            return builder;
        }

        public TBuilder AddDefaultHealthChecks()
        {
            builder.Services.AddHealthChecks()
                // Add a default liveness check to ensure app is responsive
                .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

            return builder;
        }
    }

    // This method is only for Web Applications (APIs)
    // Worker Services (Console Apps) will not call this.
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}