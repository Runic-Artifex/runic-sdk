# Tracing Bridge calls

Since 0.20, a click in the page and the C# it runs can be one trace: the page's span for the Bridge call, the
`runic.bridge.command` span of `Runic.Application.Views`, and the spans the ViewModel starts, such as its `HttpClient`
and database calls. This guide shows the C# handler timing that `dotnet runic dev` gives you without any setup, and
how to see the joined traces in a local viewer.

Everything here is for development. The page side is in the development build of `@runic-artifex/views`, which
`vite dev` resolves; a production build (`vite build`, or a Release build of a Bun-bundled frontend) contains none of
it, and its requests carry no trace context. A production host never times handlers, and reads a trace context only
while your application records spans.

## C# handler timing

Under `dotnet runic dev` the application runs with `DOTNET_ENVIRONMENT=Development`. In Development, a Bridge reply
names the C# handler that ran and how long it took, when the page's development tools ask for it. The Vite development
client of `@runic-artifex/vite-plugin-runic` asks for every call, so nothing is to configure:

1. Open the browser's developer tools on the Window (F12, or Inspect from the page's context menu) and the
   **Performance** panel. Enable **Show custom tracks** in the capture settings.
2. Record, click in the page, and stop.
3. The **Runic** track group has a **Bridge calls** track, with one entry per call and its route, request id, outcome
   and C# handler, and a **C# handlers** track below it with the handler itself, such as `EditorViewModel.Save`.

The handler's duration is measured in .NET. The page places it between sending the request and receiving its reply,
which assumes both took equally long on the way. The custom tracks are Chromium's (installed Chrome or Edge, and
WebView2); other engines get the same call reports in the development client without the Performance track. The
[Views protocol](../specs/application/README.md#handler-timing) describes the `handler-timing/1` feature.

## One trace from the page to .NET

### 1. Start a local viewer

Any OTLP/HTTP receiver works. The standalone .NET Aspire dashboard is one container and shows traces, logs and
metrics without further setup:

```sh
podman run --rm -d --name aspire-dashboard \
  -p 127.0.0.1:18888:18888 -p 127.0.0.1:4317:18889 -p 127.0.0.1:4318:18890 \
  -e ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true \
  mcr.microsoft.com/dotnet/aspire-dashboard:13
```

`docker run` takes the same arguments. The dashboard is at <http://localhost:18888>; it receives OTLP/gRPC on port
4317 and OTLP/HTTP on port 4318. `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` turns off the dashboard's sign-in and
OTLP API key, so the ports are published on loopback only: other users of this machine can still reach them, other
machines cannot. Publish them on every interface (`-p 18888:18888`) only on a network you trust, since anyone who
reaches the ports reads your traces and can send spans of their own. Stop the dashboard with `podman stop aspire-dashboard`; it
keeps telemetry in memory only.

`dotnet runic dev` starts no container or viewer of its own: start the viewer you want, once, and leave it running.

### 2. Export the .NET spans

Runic records spans with the `ActivitySource` named `Runic.Application.Views` but ships no exporter. Add the
OpenTelemetry SDK and its OTLP exporter to the application:

```sh
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

and create a tracer provider before the host runs, for example in `Program.cs`:

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;
using Runic.Application.Views;

// Exports only when an endpoint is configured, so a normal start records nothing.
using var tracing = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is null ? null
    : Sdk.CreateTracerProviderBuilder()
        .AddSource(RunicViewsTelemetry.ActivitySourceName)
        // Your own ActivitySources, such as the ViewModels'.
        .AddSource("Notes")
        .AddOtlpExporter()
        .Build();
```

An application built on the .NET generic host uses its usual `services.AddOpenTelemetry().WithTracing(...)` setup with
the same `AddSource` call. Add `AddHttpClientInstrumentation()` or your database's instrumentation to see what the
ViewModel calls under each click.

### 3. Run with the OTLP variables

Set the standard OpenTelemetry variables in the shell that runs `dotnet runic dev`, so both the application and the
Vite development server read them:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_SERVICE_NAME=Notes
dotnet runic dev
```

- The application exports its spans as service `Notes`.
- The Vite development server sees `OTEL_EXPORTER_OTLP_ENDPOINT` and prints `[runic] Exporting the page's Bridge call
  spans to http://localhost:4318/v1/traces as Notes-page`. Its development client gives each Bridge call a W3C trace
  context, which the call's request carries to .NET, and the server sends each call's span as service `Notes-page`.
  The page itself posts nothing cross-origin and loads no OpenTelemetry code.
- The server sends OTLP/HTTP with JSON, so the endpoint must be an OTLP/HTTP one (port 4318 above, not the gRPC
  port 4317). `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` makes the .NET exporter use the same endpoint.

Click in the page, then open **Traces** in the dashboard. A trace starts with the page's span, such as
`command CounterViewModel.increment` from `Notes-page`, with `command CounterViewModel.Increment` from `Notes` below
it and the ViewModel's own spans below that. The page's span has the call's route, member, outcome, reply revision and
C# handler as `runic.bridge.*` attributes.

### Options

`runic({ otlp })` in `vite.config.ts` overrides the variables:

| Value | Effect |
| --- | --- |
| absent | Export when `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` or `OTEL_EXPORTER_OTLP_ENDPOINT` is set, unless `OTEL_SDK_DISABLED=true` or `OTEL_TRACES_EXPORTER=none`. |
| `false` | Never export. |
| `true` | Export, and fail when no endpoint is set. |
| `{ url, headers, serviceName }` | Export to `url` (the full traces URL, such as `http://localhost:4318/v1/traces`), with `headers` (default `OTEL_EXPORTER_OTLP_HEADERS`, for an API key) and `serviceName` (default `OTEL_SERVICE_NAME` or the frontend's package name, with `-page`). |

The export needs the development client (`devtools` not `false`). The Angular CLI's development server
(`runicDevSessionProxy()`) has no export yet; its pages still get the C# handler timing.

### How it fits together

- `trace-context/1`: the request of a call carries `trace: { traceparent }`. The .NET host reads it only while its
  `ActivitySource` has a listener, and makes it the parent of the call's `runic.bridge.*` span. This holds outside
  Development too, by design, as for any W3C trace context a server receives: a page can choose the trace and parent
  ids of the Bridge spans in your telemetry. The values are bounded (256 characters for `traceparent`, 512 for
  `tracestate`), one that does not parse is ignored, and handler timing stays Development-only. Treat trace ids from
  the page as untrusted, as you would those of an incoming HTTP request. The ViewModel's spans
  follow, because Runic runs model turns in the `ExecutionContext` of the request that queued them.
- The page's span is a client span from the call's start (the click) to the moment the page reached the reply's
  revision. The .NET span is a server span inside it.
- The [Views protocol](../specs/application/README.md#trace-context) describes the feature, and the
  [Views README](../packages/dotnet/Runic.Application.Views/README.md#logging-and-telemetry) lists the spans and their
  tags.

## Troubleshooting

- **No page spans.** The Vite server prints no `Exporting the page's Bridge call spans` line: it did not see the
  variable, for example because it was set with `dotnet runic dev -e`, which sets it for the application only. Export it
  in the shell instead.
- **`Exporting … failed (…)` in the Vite output.** The endpoint is not an OTLP/HTTP receiver, or it is not running.
- **The .NET span is in a trace of its own.** The page that made the call loaded before the export started (reload
  it), or the call went through a host that does not implement `trace-context/1` (a 0.19 or earlier `Runic.Application.Views`).
- **No `C# handlers` track.** The application does not run in Development (`DOTNET_ENVIRONMENT`), or the page is not
  served by a development server with `runic()`.
