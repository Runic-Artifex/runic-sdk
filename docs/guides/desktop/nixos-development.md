# NixOS development and native shutdown

Run builds and tests from the SDK root in `nix develop`, or use
`nix develop --command <command>`. The locked Linux shell supplies the .NET SDK
required by `global.json`, Node, Bun, PowerShell, Clang, zlib, `pkg-config`,
Chromium, GTK, WebKitGTK and Xvfb. Playwright uses the shell's Chromium through
`PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH`; it does not need a separate browser download.
The template matrix installs its explicitly pinned npm/pnpm versions itself.

The repository's `.envrc` uses Git-aware `use flake` and watches the local `act`
patch. Avoid `use flake path:.`: it can copy ignored dependency caches and build
outputs into new Nix store snapshots. For workflow verification, use
`bun run ci --job <id>` with Docker or rootless Podman; see the
[local CI guide](../../../eng/ci/README.md) for resource limits and cleanup.

Do not assemble a substitute shell from remembered Nix store paths. In
particular, preserve the SDK wrapper, `DOTNET_ROOT`, native library paths and
browser configuration. Restricted automation can put its Nix cache in a writable
location with `XDG_CACHE_HOME=/tmp/runic-nix-cache nix develop`; ordinary
interactive development does not need that override.

For isolated KDE Plasma and GNOME portal acceptance, use the separate
[NixOS portal VMs](portal-vm.md). They run their own desktop session and portal
backend; the development shell intentionally does not start or reconfigure the
host's desktop services.

## September 7, 2026 crash investigation

Two customer example dumps had SIGSEGV in `_webui_server_thread`, inside
`pthread_mutex_lock`. They used the native library shipped with
`CsWebUi.Native 2.5.0-beta.4.4`, whose provenance pins WebUI revision
`b08e7b8b0732316c8f0d543091ee4c7b4904f4dc`.

That library's `StartServer` selects persistent server mode. Its `Destroy`
implementation can time out waiting for that server and then free the window's
mutexes while the server still accesses them. A standalone C reproduction in the
SDK Nix shell, with no .NET runtime involved, exited 139 when destroying a running
server directly. Requesting application exit before disposal exited 0.

`CsWebUiApplicationHost` now explicitly owns WebUI's process lifetime, requests
`WebUiApplication.Exit()` before disposing its window, and rejects a second host
in the same process. Stopping this host also closes any other WebUI windows in
that process. The host is not a restartable, independently owned native server.

The separate SIGABRT report came from an unhandled exception in the new mailbox
test executable. Test failures now print diagnostics and return exit code 1,
following the other integration test executables, instead of escaping `Main`.

The customer browser suite now checks the host exit code and termination signal
and rejects forced shutdown. Its lifecycle suite starts fresh host processes for
unconnected, connected and disconnected cases, including shutdown while an
active browser is polling. Both host choices run the same suite:

```sh
nix develop
CONFIGURATION=Release RunicHost=cswebui bun run verify:customers
CONFIGURATION=Release RunicHost=desktop bun run verify:customers
```

NativeAOT was also verified through the actual development shell: a fresh console
program and the repository's `AspNetCoreDiagnostic` fixture both published for
`linux-x64`. The diagnostic answered `/ping?value=nix` and shut down normally.
This replaces the earlier inconclusive result from the manually assembled
environment; it is not a cross-platform certification or a size comparison.
