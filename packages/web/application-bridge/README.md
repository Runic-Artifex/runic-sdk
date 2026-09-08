# @runic-artifex/application-bridge

Connect a TypeScript UI to a Runic Application host through typed commands, snapshots, receipts, and events. The package owns protocol validation, recovery, subscriptions, and resource lifetime so a rendering framework does not have to.

```bash
npm install @runic-artifex/application-bridge
```

Requires Node.js 24.18 or later, TypeScript, and Effect 4.0.0-rc.112. Update the Runic runtime, compiler and framework packages together, then regenerate existing application facades. This preview runtime is intended to match the generated .NET Application Bridge contract. Use [Runic.Application.Templates](https://www.nuget.org/packages/Runic.Application.Templates) for a complete app, or use this package directly with a framework adapter.

## Bootstrap a controller

New C# applications import generated schemas from the facade. For the explicit
Effect-authority alternative, define the contract once:

```ts
import { Schema } from "effect";
import {
  ApplicationBridgeLive,
  bridge,
  createApplicationBridgeController,
  createWebSocketFrameChannel,
  defineApplicationBridgeContract,
} from "@runic-artifex/application-bridge";

const Snapshot = Schema.Struct({ count: Schema.Int });
const Command = Schema.TaggedStruct("IncrementCounter", { amount: Schema.Int });
const Receipt = Schema.TaggedStruct("CounterIncremented", { count: Schema.Int });
const Event = Schema.TaggedStruct("CounterChanged", { snapshot: Snapshot });

export default defineApplicationBridgeContract({
  protocol: { identity: "example.counter", version: 1 },
  csharp: { namespace: "Example.Counter.Contract", contractName: "Counter" },
  snapshot: Snapshot,
  commands: [bridge.command(Command, { receipt: Receipt })],
  events: [Event],
  errors: [],
});
```

Run `runic-bridge generate --authority effect --source src/application.bridge.ts`, then import the generated facade when constructing
the controller:

```ts
import contract from "./application.bridge.generated";

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

Read the [Application Bridge guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/application/guides/application-bridge.md), explore [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), or report problems in [GitHub Issues](https://github.com/Runic-Artifex/runic-sdk/issues). Released under the [MIT License](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
