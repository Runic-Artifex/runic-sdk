# WebUIToolkit.Hosting.WebUi

`WebUIToolkit.Hosting.WebUi` composes the dependency-neutral browser contracts with a
scoped root MVVM session and a deterministic manifest-backed static-asset endpoint.
It declares its own `Microsoft.AspNetCore.App` shared-framework dependency for the
dependency-injection scope bridge; that dependency does not leak into the BCL-only
Hosting kernel.

The package deliberately does not discover or load a native browser runtime. Register an
explicit `IBrowserHostFactory` adapter, create a `WebUiModeRunner`, and add the matching
`FrontendAssetValidator` for `LaunchKind.UserInterface`. This keeps command launches from
resolving or initializing UI services.

The Wave C HTMX and cs-webui bindings remain separate adapters: this package exposes the
closed static-asset, root-session, and stop-notification seams they consume.

## MVVM browser transport

`MvvmWebUiTransport` is the shipping protocol-v1 boundary between a browser view and one
retained `IMvvmSession`. A fresh strict-codec handshake establishes the negotiated
capability set. Every later dispatch authenticates the session, view, and invocation
capability before reserving enough finite writer capacity for the request's complete
output. Mutations reserve both a possible atomic patch and their terminal result; the
patch is always queued first.

The transport retains only a configured finite window of terminal request tombstones.
It does not promise patch replay. Reconnect clears the old negotiation, blocks mutations,
requires a new authenticated handshake, and replaces local state from an authoritative
snapshot before mutations resume. Diagnostics are a closed enum and therefore cannot
carry session IDs, view IDs, capability tokens, payloads, paths, or exception messages.
