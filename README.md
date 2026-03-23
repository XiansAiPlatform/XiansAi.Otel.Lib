# XiansAi.Otel.Lib

`XiansAi.Otel.Lib` initializes OpenTelemetry (traces, metrics, logs) for agents/worker-style .NET processes and exports them via OTLP.
Enablement is endpoint-driven (`OPENTELEMETRY_ENDPOINT` for traces/metrics, optional `OPENTELEMETRY_LOGS_ENDPOINT` for logs).
It is designed to work with XiansAi agents but can be used by any .NET app that wants OTEL-compatible exports.

## Install

```bash
dotnet add package XiansAi.Otel.Lib
```

## Automatically captured (out of the box)

- **Traces**: outgoing HTTP via HttpClient (spans + trace propagation)
- **Metrics**: runtime + HttpClient metrics
- **Logs**: `Microsoft.Extensions.Logging` logs via OTLP (when you use the `ILoggerFactory` returned by `TelemetryBuilder.InitializeAgent(...)`)
- **Temporal traces**: captured **if** `Temporalio.Extensions.OpenTelemetry` is used by the host and the Temporal interceptor is enabled (XiansAi.Lib wires this; generic apps must enable it themselves)
- **Your own spans**: captured if your code emits `Activity` spans and you include your `ActivitySource` name/pattern in `additionalActivitySources`

## GenAI sensitive content events (optional)

If you want Semantic Kernel to include prompt/response **content** in `gen_ai.*` events (e.g. `gen_ai.event.content`), set:

- `OPENTELEMETRY_GENAI_SENSITIVE=true`

This is **off by default** because it can capture sensitive data.

## Example 1 — Add to a XiansAi agent

In your agent’s `Program.cs` (after creating `AgentTeam`), initialize OTEL with tenant-aware service name:

```csharp
using XiansAi.Flow;
using XiansAi.Otel;
using Microsoft.Extensions.Logging;

var agent = new AgentTeam("News Agent");

var loggerFactory = TelemetryBuilder.InitializeAgent(
    tenantId: AgentContext.TenantId,
    serviceName: agent.Name,
    enableLogs: true);

if (loggerFactory != null)
{
    Globals.LogFactory = loggerFactory;
}

await agent.RunAsync();
```

## Example 2 — Add to a generic .NET app

In any console/worker app:

```csharp
using XiansAi.Otel;

TelemetryBuilder.InitializeAgent(
    tenantId: "tenant-01",
    serviceName: "abcd.company01",
    additionalActivitySources: new[] { "Sample.SomePackage*" },
    additionalMeters: new[] { "Sample.SomePackage*" },
    enableLogs: true);
```

### Example (manual spans via `ActivitySource`)

If you want spans for your own code (e.g. in a package like `Sample.SomePackage.*`), create an `ActivitySource` and start activities:

```csharp
using System.Diagnostics;

namespace Sample.SomePackage;

public static class Telemetry
{
    public static readonly ActivitySource Source = new("Sample.SomePackage");
}

public static class Example
{
    public static void FuncB()
    {
        using var span = Telemetry.Source.StartActivity("FuncB");
        // do work...
    }

    public static void FuncA()
    {
        using var span = Telemetry.Source.StartActivity("FuncA");
        FuncB();
    }
}
```

To export these spans, pass your source name/pattern via `additionalActivitySources` to `TelemetryBuilder.InitializeAgent(...)`.

## Instrumentation sources/meters configuration

This library ships a default list via an embedded JSON: `Defaults/otel-defaults.json`.
To add more sources/meters, pass `additionalActivitySources` / `additionalMeters` to `TelemetryBuilder.InitializeAgent(...)`.

## Package metadata

- Package ID: `XiansAi.Otel.Lib`
- Target framework: `net9.0`
- NuGet package includes `README.md` and `LICENSE`

## Release and publish

Publishing is automated with GitHub Actions.

- Trigger: push a tag in the format `v*` (for example `v1.2.3`)
- Version source: tag value without the `v` prefix
- Feed: `https://api.nuget.org/v3/index.json`
- Credential: repository secret `NUGET_API_KEY`

The resulting artifact pushed to NuGet is:

`XiansAi.Otel.Lib.<version>.nupkg`

