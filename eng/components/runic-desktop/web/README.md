# Runic Desktop for TypeScript

The `@runic-artifex/desktop` package owns the browser-side presentation
transport. It is deliberately transport-only: Application Bridge controllers,
commands, events, and domain schemas remain in their owning packages.

Load the surface bootstrap before constructing the transport:

```html
<script src="/runic-desktop.js"></script>
```

The package's `FrameChannel` is structurally compatible with
`@runic-artifex/application-bridge` without either package depending on the
other. Compose it with the transport-neutral `ApplicationBridgeLive` layer;
the resulting controller's Effect scope owns connection and teardown while UI
framework packages remain projection-only.
