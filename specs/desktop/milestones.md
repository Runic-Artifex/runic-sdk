# Contract conformance requirements

Each requirement uses `runic.desktop.presentation/1` vocabulary and portable
conformance scenarios. Language-specific API or platform tests add evidence;
they do not redefine the contract.

## Managed hosting

The .NET core requires explicit host, surface, window, session,
request, and invocation ownership; shared-listener isolation; response
streaming and disconnect cancellation; structured payload adapters; scoped
composition; and typed security policy. The transitional `WebUi*` facade may
exercise the core only as compatibility evidence.

Required portable areas: lifecycle, streaming/cancellation, serialization,
security, errors, and shared-listener isolation.

## .NET public API

The public `Runic.Desktop` API requires shared concepts mapped into
idiomatic async-first .NET types, contains no public `WebUi*` compatibility
identity, and preserves the managed-hosting conformance results. Public API baselines and
NativeAOT/platform evidence are .NET-specific additions.

## TypeScript+Effect frontend transport

The TypeScript package requires session, transport, cancellation, streaming,
security failure, and teardown mapped into Effect services and scopes;
implements the selected versioned wire profile; and supplies the existing
Application Bridge `FrameChannel` without acquiring controller or domain
semantics.

Required portable areas: framing, serialization, cancellation, reconnect,
security, and terminal errors. The wire-profile decision must classify the
current `webui-compat/52f9e75` behavior explicitly.

## Product integration expectations

Runic Application, Assets, Translations, Vite, Svelte, Angular, templates,
examples, and the Translations Editor are product boundaries in the ownership
map. When they use Desktop, their integration uses the managed-hosting and
frontend-transport capabilities at its assigned seam rather than recreating
them.

Rust and modern C++ implementations are excluded from the v1 supported profile set.
