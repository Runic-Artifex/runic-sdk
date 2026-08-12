# @runic-artifex/application-bridge

Connect a TypeScript UI to a Runic Toolkit host through typed commands, snapshots, receipts, and events. The package owns protocol validation, recovery, subscriptions, and resource lifetime so a rendering framework does not have to.

```bash
npm install @runic-artifex/application-bridge
```

Requires Node.js 24.18 or later, TypeScript, and Effect. This preview runtime is intended to match the generated .NET Application Bridge contract. Use [RunicToolkit.Templates](https://www.nuget.org/packages/RunicToolkit.Templates) for a complete app, or use this package directly with React, Vue, Angular, or another framework.

## Bootstrap a controller

Define the contract once, then create exactly one controller at your application boundary:

```ts
import { Schema } from "effect";
import {
  CsWebUiApplicationBridgeLive,
  createApplicationBridgeController,
  createCsWebUiFrameChannel,
  defineApplicationContract,
} from "@runic-artifex/application-bridge";

const Snapshot = Schema.Struct({ count: Schema.Int });
const Command = Schema.TaggedStruct("InitializeApplication", {});
const Receipt = Schema.TaggedStruct("ApplicationInitialized", { snapshot: Snapshot });
const Event = Schema.TaggedStruct("CounterChanged", { snapshot: Snapshot });

const contract = defineApplicationContract({
  identity: "example.counter",
  version: 1,
  command: Command,
  receipt: Receipt,
  event: Event,
  snapshot: Snapshot,
  initialize: { _tag: "InitializeApplication" } as const,
});

const bridge = createApplicationBridgeController(
  contract,
  CsWebUiApplicationBridgeLive(contract, createCsWebUiFrameChannel()),
);
const snapshot = await bridge.initialize();
```

Use `CsWebUiApplicationBridgeLive` with `createCsWebUiFrameChannel()` in a native CS-WebUI application. For browser-only development and tests, use `MockApplicationBridge` instead. A controller owns one Effect runtime: create it during bootstrap, share it with the UI, subscribe once for host events, and call `dispose()` on application teardown.

## Transport and safety

The CS-WebUI channel waits for its native binding during frontend startup, returns correlated host frames through the binding response, and receives later host events through one named receiver. Frame, pending-command, and event buffers are bounded; invalid frames, protocol mismatches, and sequence gaps require authoritative recovery rather than silently changing UI state. Tune limits only after application burst testing.

Commands are named domain operations. This package deliberately does not expose generic `setProperty` or `execute` protocol operations.

Read the [Application Bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), explore [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples), or report problems in [GitHub Issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Released under the [MIT License](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
