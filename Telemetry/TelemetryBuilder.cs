using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace XiansAi.Otel;

/// <summary>
/// Entry points for configuring OpenTelemetry for XiansAi services and agents.
/// </summary>
public static class TelemetryBuilder
{
    /// <summary>
    /// Generic initialization for any process/agent.
    /// - Uses OPENTELEMETRY_ENDPOINT as the enable/disable switch (required for traces/metrics).
    /// - Uses OPENTELEMETRY_LOGS_ENDPOINT (optional) for logs; falls back to OPENTELEMETRY_ENDPOINT.
    /// - Sets OTEL service.name to "&lt;tenantId&gt;/&lt;serviceName&gt;" if tenantId is provided.
    /// - Adds any extra resource attributes provided.
    /// - Still allows OPENTELEMETRY_SERVICE_NAME to override the computed service name.
    /// </summary>
    public static ILoggerFactory? InitializeAgent(
        string? tenantId,
        string serviceName,
        IDictionary<string, object>? resourceAttributes = null,
        IEnumerable<string>? additionalActivitySources = null,
        IEnumerable<string>? additionalMeters = null,
        bool enableLogs = true,
        Func<string?>? tenantIdResolver = null)
    {
        // Optional: enable Semantic Kernel GenAI sensitive event content (prompts/responses) when explicitly requested.
        // This affects `gen_ai.*` events such as `gen_ai.event.content` and should be enabled only in trusted/dev environments.
        if (IsTruthyEnv("OPENTELEMETRY_GENAI_SENSITIVE"))
        {
            AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("serviceName must be provided", nameof(serviceName));
        }

        var computedServiceName =
            !string.IsNullOrWhiteSpace(tenantId)
                ? $"{tenantId}/{serviceName}"
                : serviceName;

        // Allow env override to win (useful for debugging and ad-hoc runs)
        var effectiveServiceName =
            Environment.GetEnvironmentVariable("OPENTELEMETRY_SERVICE_NAME")
            ?? computedServiceName;

        var instrumentation = LoadInstrumentationConfig();

        InitializeFromEnvironment(
            defaultServiceName: effectiveServiceName,
            tenantId: tenantId,
            resourceAttributes: resourceAttributes,
            additionalActivitySources: MergeList(instrumentation.ActivitySources, additionalActivitySources),
            additionalMeters: MergeList(instrumentation.Meters, additionalMeters),
            tenantIdResolver: tenantIdResolver);

        if (!enableLogs)
        {
            return null;
        }

        return BuildLoggerFactory(effectiveServiceName, tenantId, resourceAttributes);
    }


    private static bool IsTruthyEnv(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v)) return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static void InitializeFromEnvironment(
        string? defaultServiceName = null,
        string? tenantId = null,
        IDictionary<string, object>? resourceAttributes = null,
        IEnumerable<string>? additionalActivitySources = null,
        IEnumerable<string>? additionalMeters = null,
        Func<string?>? tenantIdResolver = null)
    {
        var serviceName = Environment.GetEnvironmentVariable("OPENTELEMETRY_SERVICE_NAME")
                          ?? defaultServiceName
                          ?? "XiansAi.Agent";

        var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var otlpEndpoint = Environment.GetEnvironmentVariable("OPENTELEMETRY_ENDPOINT");

        if (string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            Console.WriteLine("[OpenTelemetry] Telemetry is disabled because OPENTELEMETRY_ENDPOINT is not set. Set it to enable traces/metrics export.");
            return;
        }

        Console.WriteLine($"[OpenTelemetry] Initializing OpenTelemetry for service: {serviceName}");
        Console.WriteLine($"[OpenTelemetry] OTLP Endpoint: {otlpEndpoint}");

        try
        {
            var attrs = new Dictionary<string, object>
            {
                ["deployment.environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development",
                ["host.name"] = Environment.MachineName,
            };

            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                attrs["tenant.id"] = tenantId!;
            }

            if (resourceAttributes != null)
            {
                foreach (var kv in resourceAttributes)
                {
                    // Last write wins
                    attrs[kv.Key] = kv.Value;
                }
            }

            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(
                    serviceName: serviceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(attrs);

            var tracerBuilder = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource("XiansAi.*")
                .AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = true;
                })
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                });

            if (additionalActivitySources != null)
            {
                foreach (var src in additionalActivitySources)
                {
                    if (!string.IsNullOrWhiteSpace(src))
                    {
                        tracerBuilder.AddSource(src);
                    }
                }
            }

            // If Temporal TracingInterceptor is present, explicitly subscribe to its ActivitySources.
            // This avoids missing workflow/activity spans when wildcard patterns don't match.
            TryAddTemporalTracingInterceptorSources(tracerBuilder);

            // Tag every span with the current tenant ID when a resolver is provided.
            // For multi-tenant template agents, the resolver reads XiansContext.SafeTenantId
            // from AsyncLocal — available during any workflow/activity execution.
            if (tenantIdResolver != null)
            {
                tracerBuilder.AddProcessor(new TenantTaggingActivityProcessor(tenantIdResolver));
            }

            tracerBuilder.Build();

            var meterBuilder = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("XiansAi.*")
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                })
                ;

            if (additionalMeters != null)
            {
                foreach (var m in additionalMeters)
                {
                    if (!string.IsNullOrWhiteSpace(m))
                    {
                        meterBuilder.AddMeter(m);
                    }
                }
            }

            meterBuilder.Build();

            Console.WriteLine($"[OpenTelemetry] ✓ OpenTelemetry fully enabled for {serviceName}");
            Console.WriteLine($"[OpenTelemetry]   - Service: {serviceName} v{serviceVersion}");
            Console.WriteLine($"[OpenTelemetry]   - OTLP Endpoint: {otlpEndpoint}");
            Console.WriteLine("[OpenTelemetry]   - Note: If collector is unreachable, traces/metrics will be buffered or dropped (non-blocking)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenTelemetry] ⚠ WARNING: Failed to initialize OpenTelemetry: {ex.Message}");
            Console.WriteLine("[OpenTelemetry] ⚠ Application will continue without telemetry export");
        }
    }

    private sealed class InstrumentationConfig
    {
        public List<string> ActivitySources { get; set; } = new();
        public List<string> Meters { get; set; } = new();
    }

    /// <summary>
    /// Load default sources/meters from an embedded JSON shipped with this library.
    /// </summary>
    private static InstrumentationConfig LoadInstrumentationConfig()
    {
        var config = LoadEmbeddedDefaults();

        config.ActivitySources = config.ActivitySources.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        config.Meters = config.Meters.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        return config;
    }

    private static IEnumerable<string>? MergeList(IEnumerable<string>? a, IEnumerable<string>? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a.Concat(b);
    }

    private static InstrumentationConfig LoadEmbeddedDefaults()
    {
        try
        {
            var asm = typeof(TelemetryBuilder).Assembly;
            // Resource name ends with Defaults.otel-defaults.json (namespace prefix is compiler-generated)
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Defaults.otel-defaults.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                return new InstrumentationConfig();
            }

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return new InstrumentationConfig();
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return ParseInstrumentationJson(json) ?? new InstrumentationConfig();
        }
        catch
        {
            return new InstrumentationConfig();
        }
    }

    private static InstrumentationConfig? ParseInstrumentationJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cfg = new InstrumentationConfig();

            if (root.TryGetProperty("activitySources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in sourcesEl.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        cfg.ActivitySources.Add(el.GetString() ?? "");
                    }
                }
            }

            if (root.TryGetProperty("meters", out var metersEl) && metersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in metersEl.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        cfg.Meters.Add(el.GetString() ?? "");
                    }
                }
            }

            return cfg;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a standalone <see cref="ILoggerFactory"/> suitable for use as a replacement for any
    /// static logger factory (e.g. Xians.Lib.Common.Infrastructure.LoggerFactory.Instance).
    /// Always includes console logging. Adds OTLP log export when OPENTELEMETRY_LOGS_ENDPOINT or
    /// OPENTELEMETRY_ENDPOINT is set; otherwise returns a console-only factory (no-op for OTel).
    /// <para>
    /// Call this after env vars are loaded and assign the result to your static logger factory
    /// before any classes with static readonly ILogger fields are first loaded.
    /// </para>
    /// </summary>
    public static ILoggerFactory BuildLoggerFactory(
        string serviceName,
        string? tenantId = null,
        IDictionary<string, object>? resourceAttributes = null)
    {
        var effectiveServiceName =
            Environment.GetEnvironmentVariable("OPENTELEMETRY_SERVICE_NAME")
            ?? (!string.IsNullOrWhiteSpace(tenantId) ? $"{tenantId}/{serviceName}" : serviceName);

        var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        var logsEndpoint =
            Environment.GetEnvironmentVariable("OPENTELEMETRY_LOGS_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("OPENTELEMETRY_ENDPOINT");

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);

            builder.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "[HH:mm:ss] ";
                options.SingleLine = true;
            });

            if (string.IsNullOrWhiteSpace(logsEndpoint))
            {
                Console.WriteLine("[OpenTelemetry] Logs export disabled — OPENTELEMETRY_ENDPOINT not set.");
                return;
            }

            var attrs = new Dictionary<string, object>
            {
                ["deployment.environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development",
                ["host.name"] = Environment.MachineName,
            };

            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                attrs["tenant.id"] = tenantId!;
            }

            if (resourceAttributes != null)
            {
                foreach (var kv in resourceAttributes)
                {
                    attrs[kv.Key] = kv.Value;
                }
            }

            builder.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(
                        serviceName: effectiveServiceName,
                        serviceVersion: serviceVersion,
                        serviceInstanceId: Environment.MachineName)
                    .AddAttributes(attrs));

                options.IncludeFormattedMessage = true;
                options.ParseStateValues = true;
                options.IncludeScopes = true;

                options.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri(logsEndpoint);
                    otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                });
            });

            Console.WriteLine($"[OpenTelemetry] Logs exporter enabled → {logsEndpoint}");
        });
    }

    /// <summary>
    /// The span tag name used for tenant identity.
    /// Configurable via <c>OPENTELEMETRY_TENANT_TAG_NAME</c>; defaults to <c>tenant.id</c>.
    /// </summary>
    internal static string TenantTagName =>
        Environment.GetEnvironmentVariable("OPENTELEMETRY_TENANT_TAG_NAME")?.Trim()
        is { Length: > 0 } v ? v : "tenant.id";

    /// <summary>
    /// Tags every span with the tenant tag by calling the supplied resolver on span start.
    /// The resolver is expected to be cheap and non-throwing (e.g. reading an AsyncLocal).
    /// </summary>
    private sealed class TenantTaggingActivityProcessor : BaseProcessor<Activity>
    {
        private readonly Func<string?> _tenantIdResolver;
        private readonly string _tagName;

        public TenantTaggingActivityProcessor(Func<string?> tenantIdResolver)
        {
            _tenantIdResolver = tenantIdResolver;
            _tagName = TenantTagName;
        }

        public override void OnStart(Activity activity)
        {
            try
            {
                var tenantId = _tenantIdResolver();
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    activity.SetTag(_tagName, tenantId);
                }
            }
            catch
            {
                // Best-effort: processor must never throw or break the span pipeline.
            }
        }
    }

    private static void TryAddTemporalTracingInterceptorSources(TracerProviderBuilder tracerBuilder)
    {
        try
        {
            var debugEnabled =
                string.Equals(Environment.GetEnvironmentVariable("OTEL_TEMPORAL_DEBUG"), "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Environment.GetEnvironmentVariable("OTEL_TEMPORAL_DEBUG"), "true", StringComparison.OrdinalIgnoreCase);

            var interceptorType = Type.GetType(
                "Temporalio.Extensions.OpenTelemetry.TracingInterceptor, Temporalio.Extensions.OpenTelemetry",
                throwOnError: false);

            if (interceptorType == null)
            {
                if (debugEnabled)
                {
                    Console.WriteLine("[OTEL][Temporal] TracingInterceptor type not found while adding ActivitySources (agent tracing will miss Temporal spans).");
                }
                return;
            }

            foreach (var fieldName in new[] { "ClientSource", "WorkflowsSource", "ActivitiesSource" })
            {
                var field = interceptorType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                var activitySource = field?.GetValue(null) as System.Diagnostics.ActivitySource;
                var name = activitySource?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    tracerBuilder.AddSource(name);
                    if (debugEnabled)
                    {
                        Console.WriteLine($"[OTEL][Temporal] Subscribed to ActivitySource: {name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // best-effort: agent telemetry must not break startup
            var debugEnabled =
                string.Equals(Environment.GetEnvironmentVariable("OTEL_TEMPORAL_DEBUG"), "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Environment.GetEnvironmentVariable("OTEL_TEMPORAL_DEBUG"), "true", StringComparison.OrdinalIgnoreCase);
            if (debugEnabled)
            {
                Console.WriteLine($"[OTEL][Temporal] Failed while adding TracingInterceptor ActivitySources: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}



