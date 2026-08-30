# @runic-artifex/application-bridge

Connect a TypeScript UI to a Runic Application host through typed commands, snapshots, receipts, and events. The package owns protocol validation, recovery, subscriptions, and resource lifetime so a rendering framework does not have to.

```bash
npm install @runic-artifex/application-bridge
```

Requires Node.js 24.18 or later, TypeScript, and Effect. This preview runtime is intended to match the generated .NET Application Bridge contract. Use [Runic.Application.Templates](https://www.nuget.org/packages/Runic.Application.Templates) for a complete app, or use this package directly with a framework adapter.

## Bootstrap a controller

Define the contract once, then create exactly one controller at your application boundary:

```ts
import { Schema } from "effect";
import {
  ApplicationBridgeLive,
  createApplicationBridgeController,
  createWebSocketFrameChannel,
  defineApplicationContract,
} from "@runic-artifex/application-bridge";

const Snapshot = Schema.Struct({ count: Schema.Int });
const Command = Schema.TaggedStruct("InitializeApplication", {});
const Receipt = Schema.TaggedStruct("ApplicationInitialized", { snapshot: Snapshot });
const Event = Schema.TaggedStruct("CounterChanged", { snapshot: Snapshot });

const contract = defineApplicationContract({
  identity: "example.counter",
  version: 1,
  // Copy contractFingerprint from the generated bridge.manifest.json.
  fingerprint: "0000000000000000000000000000000000000000000000000000000000000000",
  command: Command,
  receipt: Receipt,
  event: Event,
  snapshot: Snapshot,
  initialize: { _tag: "InitializeApplication" } as const,
});

const bridge = createApplicationBridgeController(
  contract,
  ApplicationBridgeLive(contract, createWebSocketFrameChannel(
    () => new WebSocket("ws://127.0.0.1:5070/runic/bridge"),
  )),
);
const snapshot = await bridge.initialize();
```

Use `ApplicationBridgeLive` with any structural `FrameChannel`: Runic Desktop's
`createDesktopFrameChannel()` or `createWebSocketFrameChannel()` for the local
`Runic.Application.Hosting` boundary. The layer connects initially disconnected
reconnectable channels before initialization. `ApplicationBridgeLive`
is the sole production layer. For browser-only development and tests, use
`MockApplicationBridge` instead. A controller owns one Effect
runtime: create it during bootstrap, share it with the UI, subscribe once for
host events, and call `dispose()` on application teardown.

## Transport and safety

`createWebSocketFrameChannel()` is the local binary channel for `ApplicationBridgeWebSocketTransport`: reconnecting it requests a new physical connection, while a successful higher-epoch initialization remains the C# session's admission decision. Frame, pending-command, and event buffers are bounded; invalid frames, protocol mismatches, and sequence gaps require authoritative recovery rather than silently changing UI state. Rendering frameworks do not own transport, protocol revisions, cancellation, or host lifecycle.

The local WebSocket channel is not a deployed remote-service contract. Authentication, authorization, routing, TLS, remote session policy, deployment, and SSR/hydration remain outside this package boundary.

Commands are named domain operations. This package deliberately does not expose generic `setProperty` or `execute` protocol operations.

Read the [Application Bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), explore [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples), or report problems in [GitHub Issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Released under the [MIT License](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
