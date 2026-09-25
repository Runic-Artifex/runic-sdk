# Contract conformance requirements

Each requirement uses `runic.desktop.presentation/1` vocabulary and portable
conformance scenarios. Language-specific API or platform tests add evidence;
they do not redefine the contract.

## Managed hosting

The .NET core requires explicit host, surface, window, session, request, and
invocation ownership; shared-listener isolation; response streaming and
disconnect cancellation; structured payload adapters; scoped composition; and
typed security policy. The transitional `WebUi*` facade may exercise the core
only as compatibility evidence.

Required portable areas: lifecycle, streaming/cancellation, serialization,
security, errors, and shared-listener isolation.

## .NET public API

The public `Runic.Desktop` API maps shared concepts to idiomatic async-first
.NET types, contains no public `WebUi*` compatibility identity, and preserves
the managed-hosting conformance results. Public API baselines, NativeAOT, and
platform evidence are .NET-specific additions.

## Product integration expectations

Runic Application Views, Assets, Translations, Vite, Svelte, Angular, templates,
examples, and the Translations Editor are separate product boundaries. Current
Views composition uses its explicit CS-WebUI adapter. Desktop remains usable as
an independent presentation product; future integrations must use named adapter
packages and must not move application contract ownership into the Desktop core.

Rust and modern C++ implementations are excluded from the v1 supported profile set.
