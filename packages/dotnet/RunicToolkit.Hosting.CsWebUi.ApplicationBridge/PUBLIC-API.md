# Public API

`CsWebUiApplicationBridge.Attach` is the low-level one-window composition point.
Most applications use `WebUiAppBuilder.UseApplicationBridge`, supplying immutable
assets, native window policy, and a factory that creates one isolated
`ApplicationBridgeSession`.

The adapter exposes one fixed binary binding and one host receiver. Application
handlers never receive the native callback, client identity, or raw frame.
