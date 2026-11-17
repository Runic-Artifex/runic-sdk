# Environment variables

The `RUNIC_*` environment variables that the SDK's packages and tools read, and
the ones `dotnet runic dev` and the hosts set for the processes they start. Each
name keeps its meaning once it ships. **User setting** means you may set it;
**handshake** means a Runic process sets it for another one, and an application
does not set it.

**Development** says whether the variable takes effect only during
development: under `dotnet runic dev`, a Vite or Angular development server, or
an application whose environment is `Development` (`DOTNET_ENVIRONMENT` or
`ASPNETCORE_ENVIRONMENT`).

## Settings

| Variable | Read by | Purpose | Development |
| --- | --- | --- | --- |
| `RUNIC_DEV_ALLOW` | `@runic-artifex/vite-plugin-runic`; Runic Translations' Vite plugin | Addresses, CIDR networks or host names, separated by commas or spaces, that may use Runic Inspect's data and the in-page translation editor from another machine. Adds to `runic({ allow })`. See [vite-plugin-runic](../packages/web/vite-plugin-runic/README.md). | Yes |
| `RUNIC_DEV_CDP_TRACE` | `dotnet runic dev` (Linux) | A file to write the traffic of the JavaScript debugger adapter to, for reporting a debugger that does not work with it. The file holds the page's data. | Yes |
| `RUNIC_APPLICATION_VITE_DEBUG` | `dotnet runic dev` | Passed to the Vite development server as `DEBUG`, in place of `DEBUG` itself, which `dev` keeps away from the build. | Yes |
| `RUNIC_LOCALE` | `runic-app` template's `Program.cs` | The application's language at start (`RUNIC_LOCALE=de dotnet run`). Application code, which the application may change. | No |
| `RUNIC_APPLICATION_SERVE_ONLY` | Runic.Application.Views.Desktop, Runic.Application.Views.CsWebUi, Runic.Application.Testing | `1` serves the Window's page without opening a window, for browser tests and automation; the host announces the address on standard output. See [Runic.Application.Views.Desktop](../packages/dotnet/Runic.Application.Views.Desktop/README.md). | No |
| `RUNIC_APPLICATION_CLOSE_AFTER_OPEN` | Runic.Application.Views.Desktop | `1` closes the Window as soon as it opened, after writing `RUNIC_APPLICATION_OPENED=<presentation>` on standard output, for smoke tests. Warns on standard error while set. | No |
| `RUNIC_DESKTOP_REGION_TRACE` | Runic.Desktop (Windows, WebView2) | `1` writes each frame, navigation and process event of hosted regions to standard error at once, for diagnosing a region that fails. | No |
| `RUNIC_BRIDGE_CODEGEN_CACHE` | Bridge generator (`Runic.Application.Views` build) | `0`, `false` or `no` turns off the generator's output cache for the build. | No |
| `RUNIC_BRIDGE_CODEGEN_FORCE` | Bridge generator | `1`, `true` or `yes` regenerates the client although the cache is current. | No |
| `RUNIC_APPIMAGE_RUNTIME` | `dotnet runic package appimage` | The AppImage runtime file (type2-runtime's `runtime-<arch>`) when it is not beside `appimagetool`. See [packaging](packaging.md). | No |
| `RUNIC_SIGNING_PASSWORD` | `dotnet runic package` (Windows) | The password of the signing certificate, so it stays out of the command line. See [packaging](packaging.md). | No |
| `RUNIC_TEST_PLATFORM_DIRECTORY` | Runic.Application.Testing | The directory of a `RunicServeOnlyTestPlatform`, which the test sets for the application it starts (`RunicTestPlatformServiceCollectionExtensions.DirectoryEnvironmentVariable`). | No |

`dotnet runic`, `runic-create` and the Runic Assets packer are built on Runic.CommandLine,
which reads `RUNIC_COMMANDLINE_OUTPUT` and `RUNIC_COMMANDLINE_NO_INPUT`; its own
documentation describes them.

## Handshakes

`dotnet runic dev` sets these for the processes it starts; `dev -e` cannot
replace the `RUNIC_APPLICATION_VITE_*`, `RUNIC_APPLICATION_FRONTEND_*`,
`RUNIC_APPLICATION_DEV_PROJECT` and `RUNIC_APPLICATION_DEVELOPMENT_ASSETS` ones.

| Variable | Set by | Read by | Purpose | Development |
| --- | --- | --- | --- | --- |
| `RUNIC_APPLICATION_DEV_SESSION` | `dotnet runic dev` | vite-plugin-runic (`runic()`, `runicDevSessionProxy()`) | A random value per development server start, which the server returns from `/__runic/state` so `dev` knows the server on the port is its own. | Yes |
| `RUNIC_APPLICATION_DEV_PROJECT` | `dotnet runic dev` | vite-plugin-runic | The project file; the plugin looks for the Bridge generator's source map below it when `RUNIC_APPLICATION_DEV_SOURCE_MAP` names none. | Yes |
| `RUNIC_APPLICATION_DEV_SOURCE_MAP` | `dotnet runic dev` | vite-plugin-runic | The Bridge generator's development source map that `/__runic/sources` serves. | Yes |
| `RUNIC_APPLICATION_DEVELOPMENT_ASSETS` | `dotnet runic dev` | Runic.Application.Views.Desktop | The frontend's build directory, which a Window in the `Development` environment serves live and reloads from. | Yes |
| `RUNIC_APPLICATION_VITE_DEV_SERVER`, `RUNIC_APPLICATION_VITE_ENTRY`, `RUNIC_APPLICATION_VITE_PACKAGE_DIRECTORY` | `dotnet runic dev` | The Window process; Runic.Application.Views.Desktop and Runic.Application.Views.CsWebUi for the server's origin | The Vite development server's origin, entry and package directory. A Window in the `Development` environment forwards (Runic Desktop) or redirects (CS-WebUI) the `GET` and `HEAD` requests that `www/` does not answer to a loopback origin named here. | Yes |
| `RUNIC_APPLICATION_FRONTEND_DEV_SERVER`, `RUNIC_APPLICATION_FRONTEND_DEV_SERVER_KIND` | `dotnet runic dev` | The Window process; Runic.Application.Views.Desktop and Runic.Application.Views.CsWebUi for the server's origin | The Angular development server's origin and `angular`. Its origin is used like the Vite one. | Yes |
| `RUNIC_APPLICATION_FRONTEND_COMPILER_DIAGNOSTICS`, `RUNIC_APPLICATION_FRONTEND_COMPILER_HOT_RELOAD` | `dotnet runic dev` | The Window process | The files of the frontend compiler's diagnostics and hot reload snapshots. | Yes |
| `RUNIC_APPLICATION_HOST` | Runic.Application.Views build | The frontend build command | The project's `RunicApplicationHost`. | No |
| `RUNIC_WEBUI_ORIGIN`, `RUNIC_DEV_ORIGIN` | Runic.Application.Views.Dev (`runic-dev`) | The frontend development server's configuration | The serve-only backend's origin and the development coordinator's. | Yes |
| `RUNIC_APPLICATION_EXECUTABLE` | A package's launcher (the Nix builder's wrapper) | Runic.Platform.Linux.Portal | The wrapper that starts the program, so the portal registers it rather than the program behind it. Read once and removed from the environment. | No |
| `RUNIC_CALLER_UID` | Runic.Platform.MacOS | The elevated program it starts | The user ID of the application's process. | No |

## Names that are not environment variables

These `RUNIC_*` names are line prefixes on standard input or output, or
compile-time symbols. Setting them in the environment has no effect.

- Standard output of a serve-only Window: `RUNIC_APPLICATION_URL=`,
  `RUNIC_APPLICATION_WINDOW=`, and `RUNIC_APPLICATION_OPENED=` under
  `RUNIC_APPLICATION_CLOSE_AFTER_OPEN`.
- The hosted guest protocol (`specs/desktop/host-protocol.md`): `RUNIC_HOST_OFFER=`,
  `RUNIC_HOST_CHANNEL=`, `RUNIC_HOST_REFUSED=`.
- Lines between processes during development: `RUNIC_VITE_INPUTS=`,
  `RUNIC_DEV_READY|`, `RUNIC_DEV_RESTARTED|`, `RUNIC_DEV_BUILD_FAILED|`,
  `RUNIC_IDE_CONTRACT_RESTART_REQUIRED|`.
- Compile-time symbols: `__RUNIC_DEVELOPMENT__` (the development build of the
  npm packages), `RUNIC_PORTABLE_PLATFORMS`, `RUNIC_ASSETS_PACK_SUBCOMMAND`.

## Repository build and tests

The SDK's own build and tests read these; applications do not.

| Variable | Read by | Purpose |
| --- | --- | --- |
| `RUNIC_TEST_TIMEOUT_SCALE` | The SDK's tests and smokes | A positive number (default 1) that multiplies the waits of tests on slow runners ([eng/ci/README.md](../eng/ci/README.md), "Test timeouts"). |
| `RUNIC_NATIVE_SERVER_REQUIRE_ALL_RUNTIMES` | Runic.Desktop.NativeServer's project, `eng/run.mjs` | `true` requires the native server library of every runtime when packing, as CI does. |
| `RUNIC_BRIDGE_CODEGEN_TEST_INTERNAL_ERROR` | Bridge generator | Makes the generator fail internally, for its tests. |
