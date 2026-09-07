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

## Log sampling (optional)

High log volume from `Information`-level logs can be reduced. Everything below defaults to today's behavior — nothing changes unless you set these:

- `OPENTELEMETRY_LOGS_MIN_LEVEL` (default `Information`): minimum level logged at all. Set to `Warning` to turn off `Information`/`Debug`/`Trace` entirely.
- `OPENTELEMETRY_LOGS_SAMPLING_MODE` (default `ratio`): which sampling strategy to use — `ratio` or `trace`. Implemented via .NET's built-in [`Microsoft.Extensions.Telemetry` log sampling](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/log-sampling). In both modes, `Warning`/`Error`/`Critical` are never sampled, and `Trace`/`Debug` should be turned off via `OPENTELEMETRY_LOGS_MIN_LEVEL` above rather than sampled.

### Mode: `ratio` — independent decision per log line

Makes a separate random keep/drop decision for every `Information` log line. Simple, but one request's log lines can end up partially kept and partially dropped (you might see "started" and "finished" but lose the lines in between).

| Env var | Purpose | Example |
|---|---|---|
| `OPENTELEMETRY_LOGS_SAMPLING_MODE` | Leave unset, or set to `ratio` | `ratio` |
| `OPENTELEMETRY_LOGS_SAMPLING_RATIO` | Fraction of `Information` lines to keep (default `1.0` = no sampling) | `0.1` → keeps ~10% |

### Mode: `trace` — whole request kept or dropped together

Keeps or drops **all** logs for a given request together, based on whether that request's trace was sampled — you always see a request's complete story instead of a fragment, at the cost of losing entire requests instead of individual lines.

This mode requires **both** settings below — `OPENTELEMETRY_TRACES_SAMPLING_RATIO` is what actually creates the reduction; `OPENTELEMETRY_LOGS_SAMPLING_RATIO` is ignored in this mode. If you set the mode to `trace` but leave the traces ratio at its default (`1.0`, every trace kept), nothing is reduced.

| Env var | Purpose | Example |
|---|---|---|
| `OPENTELEMETRY_LOGS_SAMPLING_MODE` | Must be set to `trace` | `trace` |
| `OPENTELEMETRY_TRACES_SAMPLING_RATIO` | Fraction of requests (traces) to keep — see [Trace sampling](#trace-sampling-optional) below | `0.1` → keeps ~10% of requests |

### Which mode to pick

- Want to simply cut `Information` log volume by roughly X%, and don't mind occasional gaps in a single request's story → **`ratio`** mode.
- Want to always see a request's full, uninterrupted story when you do look at one, just fewer requests overall → **`trace`** mode.

## Trace sampling (optional)

- `OPENTELEMETRY_TRACES_SAMPLING_RATIO` (default `1.0`, i.e. every trace is kept): fraction of root traces to sample, via a `ParentBased(TraceIdRatioBasedSampler)`. A trace whose parent was already sampled in (e.g. propagated from an upstream caller) is always kept regardless of this ratio. This is also what `OPENTELEMETRY_LOGS_SAMPLING_MODE=trace` above relies on to actually reduce log volume.

## Metrics cardinality (optional)

- `OPENTELEMETRY_METRICS_CARDINALITY_LIMIT` (default `2000`, matching the OpenTelemetry .NET SDK's own built-in default): max unique tag-combinations tracked per metric instrument before the SDK collapses the rest into a single overflow series (`otel.metric.overflow=true`). Raise this if metrics are hitting overflow. Note this is a cost/precision tradeoff, not a volume reduction — it's worth checking whether an unbounded tag (e.g. a raw user or tenant ID) is driving the cardinality before just raising the cap.

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

