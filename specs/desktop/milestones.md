# Contract-bound milestone gates

Every exit gate uses `runic.desktop.presentation/1` vocabulary and portable
conformance scenarios. Language-specific API or platform tests add evidence;
they do not redefine the contract.

## M5: managed hosting foundation

M5 exits when the .NET core implements explicit host, surface, window, session,
request, and invocation ownership; shared-listener isolation; response
streaming and disconnect cancellation; structured payload adapters; scoped
composition; and typed security policy. The transitional `WebUi*` facade may
exercise the core only as compatibility evidence.

Required portable areas: lifecycle, streaming/cancellation, serialization,
security, errors, and shared-listener isolation.

## M6: .NET API reset

M6 exits when the public `Runic.Desktop` API maps the shared concepts into
idiomatic async-first .NET types, contains no public `WebUi*` compatibility
identity, and preserves the M5 conformance results. Public API baselines and
NativeAOT/platform evidence are .NET-specific additions.

## M7: TypeScript+Effect frontend transport

M7 exits when a TypeScript package maps session, transport, cancellation,
streaming, security failure, and teardown into Effect services and scopes;
implements the selected versioned wire profile; and supplies the existing
Application Bridge `FrameChannel` without acquiring controller or domain
semantics.

Required portable areas: framing, serialization, cancellation, reconnect,
security, and terminal errors. The wire-profile decision must classify the
current `webui-compat/52f9e75` behavior explicitly.

## M8: suite adoption and v1 certification

M8 exits when Runic Application, Assets, Translations, Vite, Svelte, Angular,
templates, examples, and the Translations Editor consume the M6/M7 capabilities
through the ownership map; exact C# and TypeScript+Effect packages pass the v1
golden path; and retained release evidence binds the contract identity and
supported platform profile. The readiness receipt must name the selected wire
profile and include every portable scenario and vector required by the v1
capability set; a schema-valid package graph without those results does not
pass M8.

Rust and modern C++ implementations are excluded from the M8/v1 gate.
