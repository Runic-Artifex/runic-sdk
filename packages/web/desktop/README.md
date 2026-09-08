# `@runic-artifex/desktop`

Effect-native browser transport for Runic Desktop.

```ts
import { DesktopTransportLive } from "@runic-artifex/desktop";

const transport = DesktopTransportLive();
```

The host page must load `/runic-desktop.js` first. The layer owns connection
teardown, exposes reconnect and frame streams, and maps failures to tagged
Effect errors. Its `channel` property structurally satisfies Application
Bridge's `FrameChannel`; this package intentionally has no dependency on
Application Bridge or any frontend framework.

Compose the channel with the generated contract at the application boundary:

```ts
import { createDesktopFrameChannel } from "@runic-artifex/desktop";
import {
  ApplicationBridgeLive,
  createApplicationBridgeController,
} from "@runic-artifex/application-bridge";
import { SetupContract } from "./generated/setup-contract";

export const controller = createApplicationBridgeController(
  SetupContract,
  ApplicationBridgeLive(SetupContract, createDesktopFrameChannel()),
);
```

`ApplicationBridgeLive` connects the initially disconnected channel before the
generated initialization command. Its Effect scope owns reconnection,
interruption, subscriptions, and closing the physical channel. Calling
`controller.dispose()` is therefore the one application-level teardown path.
The generated fingerprint, command ordering, cancellation, receipts, events,
and typed bridge failures remain owned by Application Bridge.

The same authenticated connection handles Desktop host JavaScript requests,
including asynchronous native close confirmation. A pending confirmation does
not block application frames. Replies from a disconnected presentation never
cross into its replacement connection. Host navigation and session closure
terminate the old channel; the destination document creates its own channel.
The Effect frame stream has a bounded buffer. A consumer that falls behind fails
with `LimitExceeded` (`frame-buffer-overflow`) and closes its channel, rather than
continuing with missing frames. Start a fresh scoped transport to recover.

The v1 implementation keeps `webui-compat/52f9e75` as an internal framing
codec. No `WebUi` identity is exported by this package.
