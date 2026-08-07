# `@runic-artifex/svelte`

Svelte 5-only projection and lifecycle support for a Runic Toolkit Application
Bridge controller.

Create the typed context once in an application module, provide it in the root
layout/component, and consume the same context below that boundary. State uses
Svelte 5 runes and immutable snapshots use `$state.raw`.

The integration never parses the protocol and never creates a second Effect
runtime. It owns only Svelte lifecycle and projection concerns.

