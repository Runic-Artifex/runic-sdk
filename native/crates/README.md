# Rust crates

The Rust crates of Runic SDK, one directory each. `native/Cargo.toml` takes every directory here as a workspace member
(`members = ["runic-server", "crates/*"]`), so a new crate needs no edit there. This file keeps the directory in Git
until the first crate lands: Cargo reads an unmatched `crates/*` as a literal path and fails.

Each crate is named `runic-artifex-<name>` (D079.5) and stays `publish = false` until crates are published (D080.4).
runic-server, the native server .NET loads, stays in `native/runic-server`; its package is `runic-artifex-server`.
