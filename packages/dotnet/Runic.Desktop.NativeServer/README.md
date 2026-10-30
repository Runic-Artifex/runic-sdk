# Runic.Desktop.NativeServer (experimental)

**Experimental ([`RUNICDESK102`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/experimental.md#RUNICDESK102)).**
This package may change or be removed in a minor release and is not part of the
SDK's release inventory yet.

Referencing it serves your Runic Desktop windows from a native HTTP and
WebSocket server written in Rust ([axum](https://github.com/tokio-rs/axum) on
Tokio and hyper) instead of ASP.NET Core Kestrel (`Runic.Desktop.Kestrel`).
Nothing else changes: the same Views protocol, bootstrap scripts, origin and
credential admission, client cookie, content handlers and asset archives run
over it, in the same `Runic.Desktop` code. An application references one of the
two server packages in place of the other; with this one it has no ASP.NET Core
in its dependencies.

```xml
<ItemGroup>
  <PackageReference Include="Runic.Desktop.NativeServer" />
</ItemGroup>
<PropertyGroup>
  <!-- Accept that the native server is experimental. -->
  <NoWarn>$(NoWarn);RUNICDESK102</NoWarn>
</PropertyGroup>
```

`RunicDesktopServer` selects the server of a project that references both
server packages, for comparing them from one project: `Native` (the default) or
`Kestrel`. Any other value than these and `None` fails with `RUNICDESK003`.

## What you get

Measured on linux-x64 with `examples/first-window-desktop`
(`tests/benchmarks/native-server/measure.sh`; the design note has the full
table):

| Publish | Kestrel | Native server |
| --- | ---: | ---: |
| NativeAOT executable | 13.06 MiB | 10.24 MiB |
| NativeAOT start until the page is served | 20.9 ms | 14.1 ms |
| NativeAOT resident memory after the page | 33.5 MiB | 24.4 MiB |
| Framework-dependent start until the page is served | 206 ms | 170 ms |

- A trimmed or NativeAOT application contains no Kestrel, hosting,
  configuration or TLS code: the `Runic.Desktop.NativeServer` runtime switch is
  a trimming feature switch.
- NativeAOT links the server statically into the executable, so the
  application stays one file.
- File responses answer a single byte range (`206`, `416`), which media
  elements need for seeking. Kestrel's path in Runic Desktop sends whole files.
- A WebSocket buffers at most 64 unread messages; a slow page holds the peer
  back instead of growing memory, and a full send queue makes the sender wait.

## What differs from Kestrel

- `DesktopHostOptions.ConfigureServices` configures a plain service collection
  with logging, without the ASP.NET Core host's configuration, environment and
  lifetime services. `ContentRequest.Services` comes from it as before.
- Only `GET` and `HEAD` reach the surface, as with Kestrel; request bodies are
  not read.
- HTTP/1.1 only, with hyper's default header limits.
- No ASP.NET Core: a framework-dependent application needs only the .NET
  runtime, and a self-contained one carries no `Microsoft.AspNetCore.*` files.
  An application that resolved `IConfiguration` or `IHostEnvironment` from
  `ConfigureServices` registers its own.

## Supported runtime identifiers

The package carries the library for `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `linux-musl-arm64`, `win-x64`, `win-arm64` and `osx-arm64`
when its CI built them all. A runtime identifier without a library fails at the
first window with `native-server-unavailable`.

## Building it

`eng/native/build-runic-server.sh [rid]` builds `native/runic-server` with the
Rust toolchain that `native/rust-toolchain.toml` pins (the repository's Nix
shell provides it) and writes `artifacts/native/runic-server/<rid>/`, from which
`dotnet pack` takes the libraries. Inside this repository, any Desktop
executable builds against it with `-p:RunicDesktopServer=Native`.
`nix build .#runic-server` builds the library from source.

The library's third-party crates are listed in `THIRD-PARTY-NOTICES.md`.
