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
  application stays one file, also when it has a Rust library of its own
  ([see below](#nativeaot-and-an-applications-own-rust-library)).
- File responses answer a single byte range (`206`, `416`), which media
  elements need for seeking. Kestrel's path in Runic Desktop sends whole files.
- A WebSocket buffers at most 64 unread messages; a slow page holds the peer
  back instead of growing memory, and a full send queue makes the sender wait.

## What differs from Kestrel

- `DesktopHostOptions.ConfigureServices` configures a plain service collection
  with logging, without the ASP.NET Core host's configuration, environment and
  lifetime services. `ContentRequest.Services` comes from it as before.
- Only `GET` and `HEAD` reach the surface's content, as with Kestrel. The one
  exception is a window's uploads (`POST {surface}/_runic/upload`, Views feature
  `blobs/1`), whose bodies stream to the window chunk by chunk (C ABI 3,
  `runic_server_read`), as Kestrel's do.
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

## NativeAOT and an application's own Rust library

The static library that NativeAOT links carries its own copy of Rust's
standard library. When the application links a Rust static library of its own
as well, the program gets two copies, and the link fails on duplicate symbols
such as `rust_eh_personality`, `std::panicking::EMPTY_PANIC` or
`ARGV_INIT_ARRAY`. `RunicDesktopNativeServerLinkage` chooses how a NativeAOT
publish gets the server instead (since 0.18):

| `RunicDesktopNativeServerLinkage` | The NativeAOT publish |
| --- | --- |
| `Static` (the default) | Links `Runic.Desktop.NativeServer.Static.<rid>`'s library into the executable. |
| `Shared` | Puts the shared library (`librunic_server.so`, `librunic_server.dylib` or `runic_server.dll`) beside the executable, which loads it at the first window, as a JIT application does. |
| `Application` | Links no library of Runic's. The application's own static library contains the `runic-server` crate and exports its C functions, which the executable binds at link time. |

The property changes nothing under the JIT (`dotnet run`, tests), which loads
the package's shared library. Any other value fails with
[`RUNICDESK006`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicdesk006).

`Shared` needs nothing else, but the application is no longer one file. On
macOS `dotnet runic package macos` signs the library with the application's
other libraries.

`Application` keeps one executable with one standard library and lets the
linker optimise the server together with the application's own Rust code
(LTO). The application's Cargo workspace takes the crate from the tag of the
`Runic.Desktop.NativeServer` version it references:

```toml
[dependencies]
runic-server = { git = "https://github.com/Runic-Artifex/runic-sdk", tag = "v0.18.0" }
```

Its static library crate names it, because Rust links no dependency that the
crate never uses, and the C functions would be missing:

```rust
use runic_server as _;
```

A library that is also built for WebAssembly, where the server does not
compile, takes the crate only for other targets: the dependency under
`[target.'cfg(not(target_family = "wasm"))'.dependencies]` and the `use` under
`#[cfg(not(target_family = "wasm"))]`.

The project sets the property beside the `NativeLibrary` and
`DirectPInvoke` items that already link the application's library:

```xml
<PropertyGroup>
  <RunicDesktopNativeServerLinkage>Application</RunicDesktopNativeServerLinkage>
</PropertyGroup>
```

Runic then adds `DirectPInvoke` for `runic_server` and, on Windows, the system
libraries Rust's standard library needs. A library without the crate fails to
link with undefined `runic_server_*` symbols.

- **Version.** At the first window Runic.Desktop checks the C ABI the library
  implements; a crate of a release with another ABI fails with
  `native-server-unavailable` ("implements ABI …, but Runic.Desktop needs
  ABI …"). Releases with the same ABI are not told apart, so move the tag and
  the package version together.
- **Toolchain and crates.** The crate needs Rust 1.98 or later (its
  `rust-version`) and Cargo resolves its dependencies in the application's
  `Cargo.lock`. `native/Cargo.lock` at the tag lists the versions Runic ships
  and tests.
- **Panics.** The application's release profile applies to the crate: Cargo
  ignores a dependency's profile. Runic builds its libraries with
  `panic = "abort"`; with the application's `panic = "unwind"`, a panic that
  reaches one of the crate's C functions still ends the process, since Rust
  aborts rather than unwind out of an `extern "C"` function, and a panic in a
  request's or connection's task ends that task, which the page sees as an
  aborted request or a closed connection. The crate's own tests run with
  unwinding.
- **Shared library of the application.** If the application's crate is also
  a `cdylib`, for its own JIT tests, that library contains the server as well,
  about 1 MiB; Runic.Desktop still loads `runic_server` under the JIT.
- **Nix.** `buildRunicApplication` keeps building the server's library from
  source for JIT builds and tests. The application's Rust derivation fetches the
  git dependency with a hash: `cargoLock.outputHashes."runic-server-0.18.0"`
  ([nix/README.md](https://github.com/Runic-Artifex/runic-sdk/blob/main/nix/README.md#the-native-desktop-server-and-an-applications-own-rust-library)).

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
