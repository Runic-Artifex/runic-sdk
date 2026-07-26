# Public API

Namespace: `WebUIToolkit.Hosting.CsWebUi.Mvvm`

- `CsWebUiMvvmBridge.Attach(WebUiWindow, IMvvmSession, CsWebUiMvvmBridgeOptions?)`
  registers one binary CsWebUi binding and transfers ownership of the session.
- `CsWebUiMvvmBridge.ConnectionIdentity` exposes the pinned CsWebUi client and
  current physical connection identifiers without exposing protocol
  capabilities.
- `CsWebUiMvvmBridge.IsClosed` reports terminal protocol or host teardown.
- `CsWebUiMvvmBridge.DisposeAsync()` removes the managed binding, cancels and
  drains active callback work, and disposes the retained MVVM transport/session.
- `CsWebUiMvvmBridgeOptions` configures the send binding, host receive function,
  and the existing bounded `MvvmWebUiTransportOptions`.
- `CsWebUiMvvmConnectionIdentity` is the immutable `(ClientId, ConnectionId)`
  value.

The package also ships the ESM `CsWebUiFrameChannel` browser adapter as a
NuGet content file.
