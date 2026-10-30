# Runic.Desktop.NativeServer

The server new Runic Desktop applications use (`dotnet new runic-app`), and the
default Desktop server from 1.0 on, with `Runic.Desktop.Kestrel` as the opt-in.

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
```

`RunicDesktopServer` selects the server of a project that references both
server packages, for comparing them from one project: `Native` (the default) or
`Kestrel`. Any other value than these and `None` fails with `RUNICDESK003`.

## What you get

Measured with `examples/first-window-desktop`
(`tests/benchmarks/native-server/measure.mjs`; serve-only, medians of 15 runs;
`tests/benchmarks/native-server/results/` has the win-x64 and osx-arm64 tables
from the `native-server.yml` workflow's platform jobs, with the same pattern:
on Windows the NativeAOT executable is 9.19 instead of 13.31 MiB and serves its
page after 18.7 instead of 33.6 ms, on macOS 8.65 instead of 11.83 MiB):

| linux-x64 | Kestrel | Native server |
| --- | ---: | ---: |
| Framework-dependent: shared frameworks the user needs | .NET + ASP.NET Core | .NET |
| Framework-dependent: start until the page is served | 214 ms | 161 ms |
| Self-contained publish | 113.7 MiB | 89.4 MiB |
| NativeAOT executable | 12.96 MiB | 9.97 MiB |
| NativeAOT start until the page is served | 21.4 ms | 12.8 ms |
| NativeAOT resident memory after the page | 33.4 MiB | 24.2 MiB |

- No ASP.NET Core in the application: Runic.Desktop has no ASP.NET Core
  reference, and this package replaces `Runic.Desktop.Kestrel`.
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

## Supported runtime identifiers and packages

The package carries the shared library for `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `linux-musl-arm64`, `win-x64`, `win-arm64` and `osx-arm64`
(≈1.2 MiB each), which a JIT application loads from `runtimes/<rid>/native/`.
A runtime identifier without a library fails at the first window with
`native-server-unavailable`.

The static libraries NativeAOT links are ≈19 MiB each, so each is a package of
its own, `Runic.Desktop.NativeServer.Static.<rid>`. The package's
`runtime.json` makes NuGet restore the one for the runtime identifier of a
runtime-specific restore (`dotnet publish -r <rid>`, which NativeAOT needs
anyway), and its targets link it and keep it out of every output. A
framework-dependent restore without a runtime identifier downloads none of
them. Without it, a NativeAOT publish fails with
[`RUNICDESK005`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicdesk005).

`RunicDesktopNativeServerLibraryDirectory` replaces the package's libraries
with a directory's `librunic_server.so`/`.dylib`/`.a` or
`runic_server.dll`/`.lib`; `buildRunicApplication` (Nix) sets it to its source
build of the library.

On macOS the shared library carries the linker's ad hoc signature;
`dotnet runic package macos` signs it again with the application's identity
and the hardened runtime, like every other library in `Contents/MacOS`. A
NativeAOT application has no separate library to sign.

## Building it

`eng/native/build-runic-server.sh [rid]` builds `native/runic-server` with the
Rust toolchain that `native/rust-toolchain.toml` pins (the repository's Nix
shell provides it) and writes `artifacts/native/runic-server/<rid>/`, from which
`dotnet pack` takes the libraries
(`packages/dotnet/Runic.Desktop.NativeServer.Static/<rid>` for each static
package). Inside this repository,
any Desktop executable builds against it with `-p:RunicDesktopServer=Native`.
`nix build .#runic-server` builds the library from source, and
`eng/native/verify-native-server-package.mjs <feed>` consumes the packed
packages as an application would.

The library's third-party crates are listed in `THIRD-PARTY-NOTICES.md`.
