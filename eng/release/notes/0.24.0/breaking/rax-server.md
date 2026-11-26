- **The native Desktop server's library is `rax_server`, C ABI 6.** `Runic.Desktop.NativeServer` ships
  `librax_server.so`, `librax_server.dylib` and `rax_server.dll` (static: `librax_server.a`, `rax_server.lib`) instead
  of `librunic_server.*` and `runic_server.*`; its C functions are `rax_server_*`, its header is `rax_server.h`, and
  the Rust package in `native/runic-server` is `runic-artifex-server`. An application that uses the packages at the
  same version needs no change. A directory named by `RunicDesktopNativeServerLibraryDirectory` must hold the new
  file names. An application that links the server into its own Rust library (`RunicDesktopNativeServerLinkage`
  `Application`) depends on `runic-artifex-server` at tag `v0.24.0` and names it with `use rax_server as _;`
  ([NativeAOT and an application's own Rust library](../../../packages/dotnet/Runic.Desktop.NativeServer/README.md#nativeaot-and-an-applications-own-rust-library)).
  A library of ABI 5 or earlier is not found under the new name and fails at the first window with
  `native-server-unavailable`.
