# RunicWindowApp

A .NET 10 desktop application with a __FRONTEND_NAME__ frontend, created from the
Runic application template. C# owns the application state and commands;
__FRONTEND_NAME__ renders it.

## Run it

```sh
dotnet tool restore
dotnet runic dev
```

`dotnet tool restore` installs the project-local `dotnet runic` tool.
`dotnet runic dev` restores NuGet packages, installs the frontend packages
with __PACKAGE_MANAGER_NAME__, builds the application, starts the
__DEVELOPMENT_SERVER_NAME__ development server, and opens the app. Frontend
edits reload in place; C# edits rebuild and restart the app. Stop it with Ctrl+C.

If something is missing, `dotnet runic doctor` checks the .NET SDK, Node.js
or Bun, __PACKAGE_MANAGER_NAME__, the lock file, and the Runic package
versions. It works before the first restore.

`dotnet build` and `dotnet run` also work without the tool. The first build
installs the frontend packages with __PACKAGE_MANAGER_NAME__, and later builds
install them again when `package.json` or the lock file changes; set
`RunicApplicationFrontendInstallEnabled` to `false` to install them yourself.
These commands build the production frontend instead of starting a development server.
On macOS, `dotnet run` and `dotnet runic dev` start the application from a
generated, ad-hoc signed development bundle in `obj/`, so notifications, login
startup and the URL schemes work while you develop; `RunicMacDevelopmentBundle`
set to `false` turns it off.

The first restore writes `packages.lock.json`, which pins every NuGet package
to its version and content hash (`RestorePackagesWithLockFile`). Commit it with
the frontend's lock file; a restore that would resolve a different package
fails until you restore again on purpose, for example with
`dotnet restore --force-evaluate` after changing a package reference.

## What opens

`RunAsync` in `Program.cs` opens `WorkspaceWindow` with
`host.OpenWindowAsync<WorkspaceWindow>()` and waits for it with
`WaitForCloseAsync()`. The Window declaration in `Views.cs` and this body are
the same on every host; only the `Run` call and its options choose the host.

<!--#if (gtk4) -->
`Program.cs` runs the application with `RunicDesktopHost.Run`, which starts a
Runic Desktop host on the event loop each platform needs, and opens the Window
in a native window with the platform's embedded WebView: the Edge WebView2
Runtime on Windows, WKWebView on macOS, or GTK 4 with WebKitGTK 6 on Linux.
`WithGtk4()` selects the GTK 4 backend and its window provider together, and
on Linux `RunicDesktopHost.Run` runs GTK on the process main thread. Linux needs
GTK 4.12 or newer and WebKitGTK 6.0 (for example `libgtk-4-1` and
`libwebkitgtk-6.0-4`); `dotnet runic doctor` lists anything missing.
Without an embedded WebView it falls back to an installed browser. See the
[Runic.Desktop.Gtk4 guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Desktop.Gtk4/README.md)
for the GTK 4 backend and platform services.
<!--#elif (desktopHost) -->
`Program.cs` runs the application with `RunicDesktopHost.Run`, which starts a
Runic Desktop host on the event loop each platform needs, and opens the Window
in a native window with the platform's embedded WebView: the Edge WebView2
Runtime on Windows, WKWebView on macOS, or GTK 3 with WebKitGTK 4.1 on Linux.
Without an embedded WebView it falls back to an installed browser. To choose
another presentation, call `DesktopEventLoop.Run` and create a
`RunicDesktopHost` with other `WindowOptions`; see the
[Runic.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Desktop/README.md)
for native prerequisites and platform services. Create the project with
`--linux-gtk 4` to use GTK 4 and WebKitGTK 6 on Linux.
<!--#if (nativeServer) -->

The page is served by the native Runic Desktop server
(`Runic.Desktop.NativeServer`), so the application needs no ASP.NET Core. To use
ASP.NET Core Kestrel instead, replace the package with `Runic.Desktop.Kestrel`
of the same version.
<!--#endif -->

Outside the `Development` environment, `Program.cs` sends a
Content-Security-Policy with the Window's pages
(`RunicDesktopHostOptions.ContentSecurityPolicy`): scripts, styles and
connections come only from the application itself, and the page can load no
plugins and cannot be framed. Without `'unsafe-eval'`, the page cannot turn
strings into code; the Views Bridge does not need it, because its state
arrives as data. `DesktopSurface.ExecuteJavaScriptAsync` evaluates its script,
so it fails under this policy. Add the origins of anything else the page
loads, such as an API or a font service, to the policy.
<!--#if (frontend == "sveltekit") -->
SvelteKit starts the page with an inline script. `kit.csp` in
`Frontend/vite.config.ts` adds a page policy that allows that script by its
hash, and the browser enforces both policies. The policy in `Program.cs` also
allows the style attribute of SvelteKit's route announcer by its hash
(`style-src-attr`); update that hash when an update of `@sveltejs/kit` changes
the announcer.
<!--#elif (frontend == "angular") -->
The policy allows inline styles, which Angular adds for component styles, and
`angular.json` turns off critical-CSS inlining, which would need an inline
script.
<!--#endif -->
<!--#else -->
`RunicCsWebUiHost.Run` in `Program.cs` uses CS-WebUI. It opens the app
in an installed browser in app mode (Chrome, Edge or another Chromium-based
browser works best; Firefox works without app mode), falls back to the default
browser, and then to the platform WebView: the Edge WebView2 Runtime on
Windows, GTK 3 with WebKitGTK 4.1 on Linux, or WKWebView on macOS.
Applications that need native windows, dialogs, or platform services can use
the template's default Runic Desktop host (create the project without
`--host`); see the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/).
<!--#endif -->

<!--#if (frontend == "sveltekit") -->
The page names no host: `Frontend/src/app.html`, SvelteKit's page template,
loads `webui.js` and `runic-views.js`, which the host package copies into
`www/` from its own Views client.
<!--#elif (frontend == "angular") -->
The page names no host: `Frontend/src/index.html` loads `webui.js` and
`runic-views.js`, which the host package copies into `www/` from its own
Views client.
<!--#else -->
The page names no host: `Frontend/index.html` loads `webui.js` and
`runic-views.js`, which the host package copies into `www/` from its own
Views client.
<!--#endif --> Switching hosts changes the host package and `Program.cs`, not
the frontend. A project that
references both hosts sets `RunicApplicationHost` to `desktop` or `cswebui`.

## File dialogs and the clipboard

`services.AddRunicPlatformServices()` in `WorkspaceServices.cs` registers
`IFileDialogs`, `ITextClipboard`, `IDesktopFileLauncher` and
`IWindowPlatformServices` (from `Runic.Platform`) for each window. A ViewModel
takes them in its constructor:

```csharp
public sealed class NotesViewModel(IFileDialogs files, ITextClipboard clipboard)
```

<!--#if (desktopHost) -->
They bind to the native window when it opens and use the provider for the
running platform and backend: Win32 on Windows, AppKit on macOS, and
xdg-desktop-portal on Linux. Before the window opens, and in a window without
a native owner (an installed-browser fallback), they return
`Unavailable(OwnerUnavailable)`. `dotnet runic doctor` names the provider and
checks for the portal on Linux. The
[Runic.Application.Views.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views.Desktop/README.md)
has an open, read and atomic-save example.
<!--#else -->
CS-WebUI windows have no native owner, so these services return
`Unavailable(OwnerUnavailable)`. The same ViewModels get native dialogs and the
clipboard on the default Runic Desktop host (create the project without
`--host`); see the
[Runic.Application.Views.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views.Desktop/README.md).
<!--#endif -->

## Project layout

| Path | Purpose |
| --- | --- |
<!--#if (viewModels == "reactiveui") -->
| `WorkspaceViewModel.cs` | ReactiveUI 26 ViewModels (the Primitives flavour): the workspace, its Welcome page, and a Counter. Their commands need no scheduler: they run on the Window's model context. |
<!--#else -->
| `WorkspaceViewModel.cs` | CommunityToolkit.Mvvm ViewModels: the workspace, its Welcome page, and a Counter. |
<!--#endif -->
| `Views.cs` | The `WorkspaceWindow` and the Views. The build generates a typed client for each. |
| `WorkspaceServices.cs` | `AddWorkspace()` registers the ViewModels, the generated `AddRunicViews()` and the platform services. |
| `Program.cs` | Opens the Window with the services from `AddWorkspace()`. |
<!--#if (desktopHost) -->
| `ApplicationIdentity.cs` | The application ID and name from the project file, and `DiagnosticsLog`, which keeps the Desktop diagnostics. |
<!--#else -->
| `ApplicationIdentity.cs` | The application ID and name from the project file. |
<!--#endif -->
| `icons/` | The application's placeholder icon: `icon.svg`, `icon.png` (512 px) and `icon.ico` (16 to 256 px). |
<!--#if (packaging) -->
| `packaging/` | The manifests `dotnet runic package` fills, which replace its built-in ones: MSIX on Windows, `.app` on macOS, the desktop entry, AppStream metadata and Flatpak manifest on Linux. See [Package it](#package-it). |
<!--#endif -->
<!--#if (frontend == "angular") -->
| `Frontend/src/app` | Components connect generated clients with `injectView()` and render the page that `WorkspaceViewModel.Main` selects with `ViewOutlet`, both from `@runic-artifex/angular`. |
<!--#elif (frontend == "sveltekit") -->
| `Frontend/src/routes/+page.svelte`, `Frontend/src/lib/pages` | The root route connects generated clients with `useView` and renders the page that `WorkspaceViewModel.Main` selects with `ViewOutlet`, both from `@runic-artifex/svelte`. `+layout.ts` prerenders it as an empty shell (`ssr = false`): it renders in the window, where the Views client connects. |
| `Frontend/src/hooks.ts` | Routes `index.html`, the file a Window opens, to the root route. |
| `Frontend/src/hooks.client.ts` | Loads the Runic development client under `vite dev`: it reports Views failures to the development server and warns about a text field that loses typed characters. The build leaves it out. |
<!--#elif (frontend == "svelte") -->
| `Frontend/src/App.svelte`, `Frontend/src/pages` | Connect generated clients with `useView` and render the page that `WorkspaceViewModel.Main` selects with `ViewOutlet`, both from `@runic-artifex/svelte`. |
<!--#elif (frontend == "vue") -->
| `Frontend/src/App.vue`, `Frontend/src/pages` | Connect generated clients with `useView` from `@runic-artifex/vue` and render the page that `WorkspaceViewModel.Main` selects. |
<!--#else -->
| `Frontend/src/App.tsx`, `Frontend/src/pages` | Connect generated clients with `useView` from `@runic-artifex/react` and render the page that `WorkspaceViewModel.Main` selects. |
<!--#endif -->
<!--#if (frontend == "sveltekit") -->
| `Frontend/vite.config.ts` | Configures SvelteKit with `runicAdapter()` from `@runic-artifex/sveltekit`, which prerenders the page into `Frontend/dist`, the folder the build copies to `www/`. SvelteKit writes relative URLs, so the page runs below any path. `runic({ html: false })` from `@runic-artifex/vite-plugin-runic` serves the Runic development client that `src/hooks.client.ts` loads, and lets `dotnet runic dev` confirm that the development server on its port is the one it started. To add its Runic DevTools dock, install `@vitejs/devtools` and register `DevTools()`. |
<!--#elif (frontend == "angular") -->
| `Frontend/proxy.conf.mjs` | The development server's proxy configuration (`proxyConfig` in `angular.json`). `runicDevSessionProxy()` from `@runic-artifex/vite-plugin-runic` lets `dotnet runic dev` confirm that the development server on its port is the one it started, and receives the reports of the Runic development client, which `src/main.ts` loads under `ng serve` only: Views failures, with their exception type, message and stack, appear in the `ng serve` terminal. Add your own proxy entries next to it. |
<!--#else -->
| `Frontend/vite.config.ts` | Adds `runic()` from `@runic-artifex/vite-plugin-runic` for Runic development diagnostics, and so that `dotnet runic dev` can confirm that the development server on its port is the one it started. To add its Runic DevTools dock, install `@vitejs/devtools` and register `DevTools()`. |
<!--#endif -->
| `Frontend/src/generated` | Typed clients generated from the ViewModels, and a typed mock for each (`*.mock.ts`). They import the shared `@runic-artifex/views` runtime. |
<!--#if (frontend == "angular") -->
| `Frontend/src/app/app.spec.ts` | A frontend test: `ng test` runs the page with Vitest against the generated mocks. See [Test it](#test-it). |
<!--#elif (frontend == "sveltekit") -->
| `Frontend/src/routes/page.test.ts`, `Frontend/vitest.config.ts` | A frontend test: Vitest runs the page against the generated mocks, with the Vite configuration and Svelte's browser build. See [Test it](#test-it). |
<!--#elif (frontend == "svelte") -->
| `Frontend/src/App.test.ts`, `Frontend/vitest.config.ts` | A frontend test: Vitest runs the page against the generated mocks, with the Vite configuration and Svelte's browser build. See [Test it](#test-it). |
<!--#elif (frontend == "vue") -->
| `Frontend/src/App.test.ts` | A frontend test: Vitest runs the page against the generated mocks. See [Test it](#test-it). |
<!--#else -->
| `Frontend/src/App.test.tsx` | A frontend test: Vitest runs the page against the generated mocks. See [Test it](#test-it). |
<!--#endif -->
<!--#if (tests) -->
| `RunicWindowApp.Tests/WorkspaceWindowTests.cs` | TUnit tests that drive the Window through `Runic.Application.Testing`. |
| `global.json` | Runs `dotnet test` on Microsoft.Testing.Platform, which TUnit uses. |
<!--#endif -->

The generated clients are not committed: `.gitignore` excludes
`Frontend/src/generated`, and `dotnet build` or `dotnet runic dev` writes
them again. Until the first build, your editor and
`__PACKAGE_MANAGER_NAME__ run typecheck` report the missing modules.
<!--#if (frontend != "angular") -->
The frontend's `build` script runs `vite build` before the type check, so
modules that a Vite plugin generates, such as the Runic Translations
`virtual:runic-translations/...` declarations, exist when the check runs on a
clean checkout.
<!--#endif -->

## Test it

The frontend test runs the page in a simulated DOM (happy-dom) against the
typed mocks that the build generates next to each client, without .NET or a
browser:

```sh
cd Frontend
__PACKAGE_MANAGER_NAME__ run test
```

It needs `Frontend/src/generated`, so build the project once first. The mocks
take their state, setter and command types from the generated clients, so a
renamed or retyped ViewModel member breaks the test at compile time. Add your
own tests next to it. The
[Testing guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/web/views/README.md#testing)
covers the mocks: failures, virtual time, latency and reconnects.

<!--#if (tests) -->
The ViewModels have a test project of their own:

```sh
dotnet test --project RunicWindowApp.Tests
```

`RunicWindowApp.Tests` drives the Window's real ViewModels and generated Bridges
in memory, as the frontend does, without a browser or native window.
`RunicWindowTestHost.Create` builds the Window from the services that
`AddWorkspace()` registers, so register new ViewModels there and both the app
and the tests use them. Tests read state by ViewModel member, set properties and
run commands:

```csharp
(await window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).ThrowIfFailed();
var counter = window.Root.View<CounterViewModel>(vm => vm.Main);
(await counter.ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();
await Assert.That(counter.Snapshot().Read(vm => vm.Count)).IsEqualTo(1);
```

TUnit runs the tests in parallel, and each test builds its own Window. The test
project builds the app with `RunicApplicationFrontendBuildEnabled=false`, so tests do not
install or build the frontend. `dotnet run --project RunicWindowApp.Tests` runs
the tests too. See the
[Runic.Application.Testing guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md)
for the drivers, a fake clock, collections and navigation.

<!--#endif -->
## State and threading

Set ViewModel properties directly in commands, also after `await`: an async
command resumes on the Window's model context, so its writes are applied one
at a time and in order. Only code that leaves the context, after
`ConfigureAwait(false)` or in `Task.Run`, commits its result with
`IRunicModelContext.InvokeAsync`. See the
[threading rule](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views/README.md#threading-state-after-await).
<!--#if (viewModels == "reactiveui") -->

## ReactiveUI 26 Primitives

This project uses ReactiveUI 26 in its default flavour, which is built on
`ReactiveUI.Primitives`, not on System.Reactive. If you know ReactiveUI from
System.Reactive:

- `RxVoid` replaces `System.Reactive.Unit`, so a command without input or
  output is a `ReactiveCommand<RxVoid, RxVoid>`.
- `ISequencer` replaces `IScheduler`: `RxSchedulers.MainThreadScheduler`, formerly
  `RxApp.MainThreadScheduler`, is an `ISequencer`.
- To await a command, add `using ReactiveUI.Primitives.Signals;`; without it,
  `await command.Execute()` does not compile (CS1061).
  `command.Execute().ToTask()` from `ReactiveUI.Primitives` also works and
  returns the last value.

`AddRunicReactiveModelContext()` in `Program.cs` makes ReactiveUI's main-thread
scheduler deliver a command's results and `IsExecuting` on the model context
of the Window that runs it. Create commands without a scheduler, as
`WorkspaceViewModel.cs` does. Code that schedules from outside a model turn, for
example after `ConfigureAwait(false)`, runs on ReactiveUI's default scheduler;
Runic logs that once. For System.Reactive, use `Runic.Application.Views.ReactiveUI.Reactive`
instead; see the
[ReactiveUI adapter](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md).
<!--#endif -->

## Read-only and settable state

As in any MVVM View, the C# setters decide what the frontend may set. The
Counter's `Count` has a private setter, so the page only reads it; `Step` and
`Label` have public setters, so the generated client has `setStep` and
`setLabel`, and the generated `CounterFields` has a field descriptor for each.
The page binds them with the framework's field binding (`useField`, or
`injectField` in Angular), which writes what the user types, keeps it while
the writes are on their way, parses numbers, and reports the member's
validation messages. Pass `{ commit: "pause" }` to write after a pause and
when the field loses focus instead of on every input. Give status such as `IsDirty`, `Error` or a collection a private setter
<!--#if (viewModels == "reactiveui") -->
(`private set => this.RaiseAndSetIfChanged(...)`, or `[Reactive]` with
`{ get; private set; }`), and keep form fields publicly settable.
<!--#else -->
(`[ObservableProperty] public partial T Name { get; private set; }`), and keep
form fields publicly settable.
<!--#endif -->

## Security

The page renders the application, but treat what it sends as untrusted input:
any script that runs in it can call every public setter and command of the
Window's ViewModels. Check values in C#, keep state that the page only reads
behind private setters, and let the user choose files and confirm destructive
steps in native UI instead of taking paths from the page. The
[security guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/docs/security.md)
covers embedded windows and browsers, admission, Content-Security-Policy,
permissions and what to keep out of the frontend build.

## Identity and icons

The project file declares the application's identity once:

| Property | Value | Used for |
| --- | --- | --- |
| `ApplicationId` | `__RUNIC_APPLICATION_ID__` | The desktop entry, icon and Flatpak ID on Linux, the bundle identifier on macOS, the package name and AppUserModelID on Windows. |
| `ApplicationTitle` | `__RUNIC_APPLICATION_NAME__` | The name users see. |
| `Version` | `1.0.0` | The application's version. |
| `ApplicationIcon` | `icons/icon.ico` | The executable's icon in Explorer and the taskbar. |

Choose the ID before you ship: a reverse-DNS name for a domain you control,
such as `org.example.Notes` (`dotnet new runic-app --application-id org.example.Notes`
sets it). Use ASCII letters, digits and dots, and start each part with a
letter, so every platform accepts it; changing it later makes the platforms see
a different application. `ApplicationIdentity.Id` reads it from the assembly.
<!--#if (gtk4) -->
Program.cs passes it to `WithGtk4`, so the desktop matches the windows to the
desktop entry of that name.
<!--#endif -->
<!--#if (desktopHost) -->
To show notifications, pass it to `AddRunicPlatformServices(ApplicationIdentity.Id)`
once the application is installed: the platforms accept it only from an
installed application (a desktop entry of that name, a registered
AppUserModelID or an MSIX package, a macOS bundle).
<!--#endif -->

The icon in `icons/` is a neutral placeholder. Replace `icons/icon.svg` with
your own square icon, then render `icon.png` (512 px) and `icon.ico` from it,
for example with ImageMagick:

```sh
magick -background none -density 384 icons/icon.svg -resize 512x512 icons/icon.png
magick icons/icon.png -define icon:auto-resize=256,128,64,48,32,24,16 icons/icon.ico
```

## Publish

```sh
dotnet publish -c Release -r linux-x64
```

Use `win-x64`, `osx-arm64`, or another runtime identifier for other
platforms. Windows builds are GUI applications (`WinExe`), so no console window
opens.
<!--#if (desktopHost) -->
Desktop diagnostics, such as a missing WebView runtime, still go to standard
error, which a terminal or a redirect (`RunicWindowApp.exe 2> errors.txt`)
shows, and to `diagnostics.log` in a folder named after the application ID
under the user's local application data (`%LOCALAPPDATA%` on Windows,
`~/.local/share` on Linux, `~/Library/Application Support` on macOS). The log
starts empty at each start and keeps at most 256 KiB of diagnostics, without
arguments or data.
<!--#endif -->
The
publish folder holds the executable and a `www` folder with the built frontend;
ship those two together, without debug symbols. Users need no Node.js or package
manager, only the platform WebView or a browser described above. The
[Ship a Desktop application](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/shipping.md)
guide covers publish modes, signing and packaging per platform.

## Package it

`dotnet runic package <target>` packages the application on the platform it
runs on. It reads `ApplicationId`, `ApplicationTitle`, `Version`,
`Description`, `PackageProjectUrl`, `RunicApplicationPublisher`,
`RunicApplicationUrlSchemes`, `RunicApplicationFileTypes`,
`RunicApplicationWebLinkHosts` and `RunicApplicationDBusActivatable` from the
project file, publishes with NativeAOT, fills its manifests and writes the
package to `artifacts/package/<target>/`:

```sh
dotnet runic package windows    # MSIX, on Windows with the Windows SDK
dotnet runic package macos      # .app in a disk image, on macOS with Xcode's command line tools
dotnet runic package flatpak    # Flatpak, on Linux with flatpak-builder
dotnet runic package appimage   # AppImage, on Linux with appimagetool
dotnet runic package deb        # Debian package, on Linux with dpkg-deb
dotnet runic package rpm        # rpm, on Linux with rpmbuild (also on Debian and Ubuntu)
```

<!--#if (packaging) -->
The manifests in `packaging/` replace the command's built-in ones: edit them to
change a package. A manifest you delete falls back to the built-in default.
<!--#else -->
To edit the manifests, create the project with `--packaging`, which copies the
command's built-in manifests to `packaging/`, where they replace the defaults.
<!--#endif -->
`RunicPackageIcon` names the 512 px icon (`icons/icon.png`; the `icon.svg`
beside it is the scalable icon on Linux) and `RunicPackageName` the deb and rpm
package name (the lower-cased assembly name). `--format json` reports what was
written. See
[Package the application](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md#package-the-application)
and the [packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/packaging.md)
for each format's tools and for signing.

| Platform | Package | Without credentials | To ship it |
| --- | --- | --- | --- |
| Windows | `<id>_<version>_x64.msix` from `windows/AppxManifest.xml` | Unsigned; Windows installs only signed packages. Sign it with a test certificate to try it. | A code-signing certificate whose subject is `RunicApplicationPublisher`: `--certificate app.pfx` (password in `RUNIC_SIGNING_PASSWORD`) or `--thumbprint <sha1>` from the certificate store, and `--timestamp-url` from your provider. A cloud signing service signs the unsigned package with its own `signtool` options. The Microsoft Store signs packages itself. |
| macOS | `<name>.app` and `<name>-<version>.dmg` from `macos/Info.plist` and `entitlements.plist` | Ad-hoc signed with the hardened runtime: it runs on this Mac; Gatekeeper blocks it elsewhere. | An Apple Developer Program membership with a Developer ID Application certificate in the keychain (`--sign "Developer ID Application: Example Ltd (TEAMID)"`), and notarization credentials stored once with `xcrun notarytool store-credentials <profile>` (`--notary-profile <profile>`), which notarizes and staples the disk image. Apple silicon (`osx-arm64`) only; Runic does not target Intel Macs. For the App Sandbox and the Mac App Store, `--sandbox` (or `RunicMacSandbox`), `RunicMacApplicationCategory`, `--provisioning-profile` and `--pkg` with `--installer-sign`; see the packaging guide. |
| Linux | `<id>.flatpak` and a repository from `linux/flatpak.yml`, `app.desktop` and `app.metainfo.xml` | Unsigned: install the bundle with `flatpak install --user <id>.flatpak`. | A GPG key for your own repository (`--gpg-sign <key id>`). |
| Linux | `<Name>-<version>-<arch>.AppImage` | Unsigned; it uses the system's GTK and WebKitGTK. | `appimagetool --sign` with your key; see the packaging guide. |
| Linux | `<package>_<version>_<arch>.deb` and `<package>-<version>-1.<arch>.rpm` | Unsigned; they depend on the distribution's GTK and WebKitGTK. | Sign the apt repository, or the rpm with `rpmsign`. |

The Flatpak runs on the GNOME runtime, which provides GTK 4 and WebKitGTK 6,
so create the project with `--linux-gtk 4` for it. Inside Flatpak the
application ID is `FLATPAK_ID`, which `WithGtk4` accepts when it matches. The
deb and rpm depend on GTK 3 and WebKitGTK 4.1, or on GTK 4 and WebKitGTK 6 with
`--linux-gtk 4`.

### Identity on each platform

- **Windows**: the package name is the application ID, and Windows registers
  the AppUserModelID `<package family name>!App` that notifications use; Runic
  uses it in the package. An unpackaged copy (a ZIP or another installer)
  registers its AppUserModelID itself, with a Start menu shortcut whose
  `System.AppUserModel.ID` is the ID. The manifest declares a
  `desktop:StartupTask` (`TaskId="LoginStartup"`, off until the application
  turns it on), which Runic's login startup uses in the package.
- **macOS**: the bundle identifier is the application ID, and the frontend is
  in `Contents/Resources/www`, where Runic finds it.
- **Linux**: the desktop entry, the AppStream metadata and the icons are named
  after the ID.

Once installed, pass the ID to `AddRunicPlatformServices(ApplicationIdentity.Id)`
for notifications.

### Opened URLs and files

`RunicApplicationUrlSchemes` (for example `org.example.notes` for an OAuth
redirect to `org.example.notes:/callback`) adds the scheme to every package:
a `windows.protocol` extension in the MSIX manifest, `CFBundleURLTypes` in
`Info.plist` and `x-scheme-handler/<scheme>` in the desktop entry. Windows and
Linux start the application with the URL as its argument; macOS sends it as
an Apple event.

`RunicApplicationFileTypes` lists the file types the application opens, such
as `.notes=application/x-example-notes;.md`: an extension with its dot and,
optionally, its MIME type. In `Info.plist` each becomes a document type. An
extension with a system type, such as `.txt` (`public.plain-text`), `.md`
(`net.daringfireball.markdown`) or `.png` (`public.png`), names that type with
the `Alternate` rank, so the application is offered for such files without
taking them over; `RunicMacFileTypeHandlerRank` set to `Default` asks to be
their default application instead. Any other extension is the application's
own: an exported uniform type identifier `<ApplicationId>.<extension>` that
conforms to `public.data`, with the `Owner` rank. For a standard type the
command does not know, name it with `RunicMacFileTypeIdentifiers`, such as
`.log=public.log` (see the [packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/packaging.md#file-types)).
In the MSIX manifest each becomes a `uap3:FileTypeAssociation` with
`MultiSelectModel="Player"`, so files the user opens together reach one
process; read them with `DesktopPlatformServices.GetLaunch(args)`. In the
desktop entry each is a MIME type (its own, or
`application/x-<last part of the ID>-<extension>`), whose shared-mime-info
definition the Linux packages install.

`RunicApplicationDBusActivatable` set to `true` adds `DBusActivatable=true` to
the desktop entry and a D-Bus service file to the Flatpak, deb and rpm (not to the
AppImage, which has no fixed path): GNOME and KDE then
start the application through the session bus and deliver later opens to the
running process. The application must then serve the activation, after
claiming its instance:
`await DesktopPlatformServices.ExportApplicationAsync(ApplicationIdentity.Id, instance)`.

`RunicApplicationWebLinkHosts` lists hosts, such as `notes.example.org`, whose
`https` links open the application; pass the same hosts as
`DesktopOpenOptions.WebLinkHosts`. On macOS these are universal links: sign the
bundle with the `com.apple.developer.associated-domains` entitlement
(`applinks:notes.example.org`), which needs a provisioning profile from your
Apple Developer account embedded as `Contents/embedded.provisionprofile`, and
serve `https://notes.example.org/.well-known/apple-app-site-association` with
`{"applinks":{"details":[{"appIDs":["TEAMID.<ApplicationId>"],"components":[{"/":"/*"}]}]}}`.
An ad-hoc signed bundle cannot carry the entitlement, so `dotnet runic package`
does not add it. On Windows these are app URI handlers: the MSIX manifest lists each
host in a `uap3:AppUriHandler`, and each host serves
`https://notes.example.org/.well-known/windows-app-web-link` with
`[{"packageFamilyName":"<family name>","paths":["*"]}]`. Treat opened URLs and files as untrusted input.
<!--#if (nix) -->

## Build it with Nix

`flake.nix` builds the application with the Runic SDK's
`buildRunicApplication`, in the Nix sandbox and without network access. NuGet
packages come from `packages.lock.json`, which `dotnet restore` writes because
the project sets `RestorePackagesWithLockFile`, and for the NativeAOT package
from `packages.aot.lock.json`, which `dotnet restore -p:PublishAot=true`
writes. Both include `linux-x64` (`RuntimeIdentifiers`), and the build
restores in locked mode: after changing a package reference, restore both
again, or the build fails with `NU1004`. The frontend's packages come from its
lock file. Commit the lock files, and add the files to Git before the first
build, because a flake sees only the files Git tracks:

```sh
dotnet restore
dotnet restore -p:PublishAot=true
<!--#if (tests) -->
dotnet restore RunicWindowApp.Tests
<!--#endif -->
git init && git add -A
nix build                # or nix build .#native-aot
result/bin/RunicWindowApp
```

<!--#if (tests) -->
The test project has its own lock file, `RunicWindowApp.Tests/packages.lock.json`.
Both packages build and run the tests, under the JIT, so a failing test fails
the build; `nix flake check` builds the default package. The build runs the
test application itself (`testRunner = "Microsoft.Testing.Platform"`), since
`dotnet test`'s VSTest options do not apply to TUnit.

<!--#endif -->
<!--#if (packageManager != "npm") -->
The frontend's packages are one fixed-output download. Its hash in
`flake.nix` is `lib.fakeHash` until the first `nix build`, which fails and
reports the real hash: replace `lib.fakeHash` with it, and update it whenever
the lock file changes. `nix run .#default.fixedOutputHashes` prints it too.

<!--#endif -->
The package installs the program in `bin`, the desktop entry
`share/applications/__RUNIC_APPLICATION_ID__.desktop` with the icons from
`icons/` under the same name, and GTK and WebKitGTK as run-time libraries.
`flake.nix` repeats the application ID; change it there too when you change
`ApplicationId`. The build reads `RunicApplicationFileTypes` and
`RunicApplicationUrlSchemes` from the project: it installs the file types'
MIME definitions and adds them and the URL schemes to the desktop entry's
`MimeType=`, as `dotnet runic package` does. `nix develop`
opens a shell with the .NET SDK, Node, the package manager and those libraries.
The SDK's
[Nix packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/nix/README.md)
covers icons, privileged actions, NativeAOT, and the NixOS and Home Manager
modules.
<!--#endif -->

## Project settings

The Runic packages supply defaults, so the project file only opts in with
`RunicApplicationFrontendWindowProject`. Set these properties to change them:

| Property | Default |
| --- | --- |
| `RunicApplicationFrontendDirectory` | `Frontend` |
| `RunicApplicationFrontendGeneratedDirectory` | `Frontend/src/generated` |
| `RunicApplicationFrontendPackageManager` | `packageManager` in `Frontend/package.json`, then the lock file |
| `RunicApplicationFrontendBuildCommand` | `<package manager> run build` |
| `RunicApplicationFrontendInstallEnabled` | `true`: install frontend packages during build when they are missing or `package.json` or the lock file changed |
| `RunicApplicationFrontendBuildEnabled` | `true`; `dotnet runic dev` sets `false` while its development server runs |

See the [Runic.Application.Views package guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views/README.md)
for every build property, and the
[examples](https://github.com/Runic-Artifex/runic-sdk/tree/v__RUNIC_NUGET_VERSION__/examples)
for nested Views, routing, and multiple windows.
