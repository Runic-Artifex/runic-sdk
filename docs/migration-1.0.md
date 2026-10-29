# Upgrading from 0.9 to 1.0

This guide collects what an application written against Runic SDK 0.9.x changes
to run on 1.0. 0.10.0 made every breaking change planned before 1.0; 0.11.0 only
added APIs but changed some defaults; 0.12.0 changed macOS behaviour and macOS
packaging, which needed breaking changes; 0.13.0 replaced Views protocol 3 with
protocol 4, which 1.0 keeps; the `1.0.0-rc.N` releases and 1.0.0 take fixes only.
An application already on 0.12 needs only [From 0.12 to 0.13](#from-012-to-013),
and one on 0.11 also [From 0.11 to 0.12](#from-011-to-012). Each section links
the release notes, which give the full detail and the pull requests:

- 0.10.0: [Upgrading](../eng/release/notes/0.10.0.md#upgrading),
  [Behaviour changes](../eng/release/notes/0.10.0.md#behaviour-changes),
  [Security](../eng/release/notes/0.10.0.md#security)
- 0.11.0: [Upgrading](../eng/release/notes/0.11.0.md#upgrading),
  [Behaviour changes](../eng/release/notes/0.11.0.md#behaviour-changes),
  [Security](../eng/release/notes/0.11.0.md#security)
- 0.12.0: [Upgrading](../eng/release/notes/0.12.0.md#upgrading),
  [Behaviour changes](../eng/release/notes/0.12.0.md#behaviour-changes),
  [Breaking changes](../eng/release/notes/0.12.0.md#breaking-changes)
- 0.13.0: [Upgrading](../eng/release/notes/0.13.0.md#upgrading),
  [Breaking changes](../eng/release/notes/0.13.0.md#breaking-changes),
  [Behaviour changes](../eng/release/notes/0.13.0.md#behaviour-changes)

## Before you start

1. Update every Runic NuGet and npm package to the same release, then rebuild and
   regenerate. Views protocol 4 is a strict match: a page and a .NET host from
   different protocol versions do not connect (`BridgeError` kind `failed` with
   one `protocol` diagnostic). `Runic.Application.Views.CsWebUi` keeps a
   prerelease version of its own; take the one listed in the release notes.
2. Remove every `RUNICDESK100`, `RUNICDESK101` and `RUNICASSETS001` suppression
   and every `./experimental` import (see [npm packages](#npm-packages)). No .NET
   or npm API is experimental.
3. Commit your frontend's lock file (see [MSBuild](#msbuild)).
4. Builds with `TreatWarningsAsErrors` meet the new warnings `RUNICNAV002`,
   `RUNICBRIDGE029` and `RUNICDBUS013`; see the [diagnostics catalog](diagnostics.md).

Compatible versions: Runic Command Line 0.9 for the SDK tools (no SDK library
depends on it), CsWebUi 2.5.0-beta.4.8 or later, Vite `>=8.3.3 <9`, TypeScript 5.2
or later, Node `^24.18.0 || ^26.0.0` (Node 24 is the certified version).
`Runic.Desktop` now brings `Runic.Platform` and `Runic.Platform.Linux.DBus` at its
own exact version, and Tmds.DBus.Protocol, on every OS.
On macOS, Runic supports macOS 15 or later on Apple silicon (`osx-arm64`) only;
see [From 0.11 to 0.12](#from-011-to-012).

## MSBuild

| 0.9 | 1.0 |
| --- | --- |
| `<RunicApplicationFrontendReactiveUiFlavor>` or `<RunicBridgeReactiveUiFlavor>` | Remove it (`RUNICBRIDGE017`). The flavor follows the adapter you reference: `Runic.Application.Views.ReactiveUI.Reactive` for System.Reactive |
| Mixed ReactiveUI flavors that arrive through packages or projects, or `Runic.Application.Views.ReactiveUI` with `Runic.Navigation.ReactiveUI.Reactive` | `RUNICBRIDGE023`: use one flavor in every Runic ReactiveUI adapter |
| A ReactiveUI command without a Runic ReactiveUI adapter: CS0234 in generated code | `RUNICBRIDGE025`; reference `Runic.Application.Views.ReactiveUI` or `.Reactive` |
| Filters on `RUNICBRIDGE001` for generator crashes | `RUNICBRIDGE024` |
| No frontend lock file: the build ran `npm install --ignore-scripts` | `RUNICBRIDGE026`. Commit `package-lock.json`, `pnpm-lock.yaml` or `bun.lock`, or set `RunicApplicationFrontendAllowUnlockedInstall=true` |
| `npm-shrinkwrap.json` only: `npm install` | `npm ci --ignore-scripts` |
| `RunicApplicationFrontendPackageManager` other than `npm`, `pnpm`, `bun` used npm silently | Warning `RUNICBRIDGE029` |

```sh
cd Frontend && npm install && git add package-lock.json
```

Details: 0.10.0 [Build properties and build errors](../eng/release/notes/0.10.0.md#build-properties-and-build-errors),
0.11.0 [Build properties](../eng/release/notes/0.11.0.md#build-properties).

## Views

Generated bridges are `internal sealed` in `Runic.Application.Views.Generated.Bridges`.
Code that constructs one, including tests through `InternalsVisibleTo`, adds the
namespace:

```csharp
using Runic.Application.Views.Generated.Bridges;

new RunicWindowTestHost<ShellViewModel>(shell, (t, c, vm) => new ShellBridge(t, vm, content: c));
```

| 0.9 | 1.0 |
| --- | --- |
| `BridgeProtocol.Version` 2; telemetry and testing `ErrorKind` `disconnected` for a released View | 4 (3 in 0.10 to 0.12; see [Views protocol 4](#views-protocol-4)); `released` (`RunicCallReply<T>.ErrorKind`, `RunicViewPublication.ErrorKind`, `RunicViewDriver.Mount`/`Unmount` messages) |
| `ObserveOn(ReactiveSchedulers.For(context))` added after a plain `command.Execute()` in a turn | Remove it: the result, `IsExecuting`, `CanExecute` and `ThrownExceptions` arrive on the turn's model context. Don't block a turn on them (it deadlocks) |
| CPU-heavy `MainThreadScheduler` work after `ConfigureAwait(false)`, in `Task.Run` or from a timer created in a turn | Runs on that turn's model context; move it to `TaskPoolScheduler` |
| Event 1042 logged at once for a fire-and-forget failure in a Bridge call | Logged when the outermost Bridge call ends; use `ObserveBridgeExceptions` where your code handles a bridged command's exception itself |
| Setter values and command arguments unlimited; checked writes, operation starts and interactions 64 KiB | All limited to the window's input limit, 1 MiB UTF-8 by default; raise `MaximumInputBytes` (`RunicWindowOptions`, `RunicDesktopHostOptions`, `RunicCsWebUiHostOptions`) if a page sends more. Also: numbers 4 096 characters, 64 awaited command calls per window, presentation and other identifiers 128 characters (mount tokens of 256 characters in 0.10 to 0.12) |
| A `ReactiveValidationObject` property message repeated at `[]` | `[]` holds only object-level rules; Toolkit object-level results that name a member appear once, at the member |
| A reply could arrive before the call's state frames on other Views | The reply waits up to 5 s for them |
| Views state sent to the page as a script | Sent as data on the connection's push channel, which `__runicBridge.onMessage` delivers (`__{route}Changed(state)` in 0.10 to 0.12) |

`IRunicWindowHost.Validate` and the Views.Desktop menu commands follow the menu
changes in [Desktop and hosts](#desktop-and-hosts).

Details: 0.10.0 [Views (.NET)](../eng/release/notes/0.10.0.md#views-net) and
[Testing](../eng/release/notes/0.10.0.md#testing); 0.11.0
[Behaviour changes](../eng/release/notes/0.11.0.md#behaviour-changes).

## Navigation

0.10 and 0.11 only add Navigation APIs (`RunicNavigator.IsBusy`, non-generic
`NavigationRegion.BackToAsync`/`ClearHistoryAsync`). Start a WPF application's
first page synchronously, so that a construction failure throws at startup:

```csharp
// 0.9: async void OnStartup … await Main.ResetAsync<HomeViewModel>();
Main.Start(NavigationTarget.Create<HomeViewModel>());
window.Show();
```

`RUNICNAV002` is a new warning. Details: 0.10.0
[Navigation](../eng/release/notes/0.10.0.md#navigation).

Tabs that each keep their own stack of pages need no tab models since 0.12. The
page owns the tab regions and a `NavigationSelection` among them
(`navigator.CreateSelection(this, [a, b])`), and WPF binds it with
`rn:NavigationSelector.Selection` instead of `rn:NavigationSelector.Region`.
Switching tabs then asks the pages of the tab left, and retiring the page or
closing the window asks every tab. Keep `NavigationSelector.Region` for
borrowed models without stacks of their own, such as settings pages. See
[Selecting tabs](../packages/dotnet/Runic.Navigation.Wpf/README.md#selecting-tabs).

## Desktop and hosts

### Availability

| 0.9 | 1.0 |
| --- | --- |
| `DesktopPlatform.IsEmbeddedWindowAvailable` | `DesktopPlatform.GetAvailability(options).Embedded.IsAvailable` |
| `DesktopPlatform.IsBrowserAvailable(browser, folder)` | `DesktopPlatform.GetAvailability(new() { BrowserFolder = folder }).GetPresentation(browser).IsAvailable` |
| `GetAvailability(folder[, linux])` | `GetAvailability(new DesktopHostOptions { BrowserFolder = folder, Linux = linux })` |
| `host.GetAvailability()` | `DesktopPlatform.GetAvailability(options)`, or `host.Validate(windowOptions)` for one window |
| `DesktopPlatform.GetLinuxEmbeddedBackends()` | `DesktopPlatform.GetAvailability().LinuxBackends` |
| `Capabilities == <flags>` | `HasFlag`: the set gains `Announcements` and, per OS, `State`/`FileDrop` |

### Surface and custom hosts

| 0.9 | 1.0 |
| --- | --- |
| `surface.RegisterCapability(…)`, `PresentationResult` | ViewModel commands and operations through Views (`Runic.Application.Views.Desktop`) |
| `surface.RunJavaScriptAsync(script)` | `await surface.ExecuteJavaScriptAsync(script + "; return '';")` (`ValueTask<string>`; call `.AsTask()` before `WaitAsync`) |
| `surface.NavigateAsync(url)`, `SendAsync`, `PresentationSession` | No public replacement; navigate with the window's content or through Views |
| `SubscribeConnectionEvents(invocation => …)`, `PresentationEventKind` | `SubscribeConnectionEvents(connection => …)` with `DesktopConnectionEvent`, `DesktopConnectionEventKind`; count pages with `ConnectionCount` |
| `IDesktopWindowHostFactory` and the custom host types behind `RUNICDESK100` | Stable; remove the suppressions. Hosts receive `DesktopWindowHostOptions.WebContent` and `AdditionalOrigins` |

### Menu bar

The menu bar uses the `Runic.Platform` menu model (see [Platform](#platform)).

| 0.9 | 1.0 |
| --- | --- |
| `Runic.Desktop.DesktopMenu(title, IReadOnlyList<…>)` | `Runic.Platform.DesktopMenu(title, ImmutableArray<DesktopMenuEntry>)`; write `[.. list]` for a list |
| `new DesktopMenuBarItem(title, "file.save")`, `DesktopMenuBarSeparator` | `new DesktopMenuItem(title, "file.save")`, `DesktopMenuSeparator` |
| `Runic.Desktop.DesktopMenuEntry`, `DesktopMenuRole`, `DesktopShortcut`, `DesktopShortcutModifiers` | Same names in `Runic.Platform` |
| `IDesktopMenuCommandTarget.CanExecute(string)`/`Execute(string)` | `CanExecute(DesktopMenuItem)`/`Execute(DesktopMenuItem)`; read `item.CommandId` |
| A declared `DesktopHostOptions.MenuBar` only in the page or on macOS | Shown natively in WebView2, GTK 3 and GTK 4 windows; the client area shrinks. Keep the old look with `DesktopWindowOptions.MenuBarPresentation = DesktopMenuBarPresentation.Page` (or `None`) |
| A GTK window always kept its bar | On X11 and KDE Plasma Wayland it moves into the global menu; `DesktopHostOptions.Linux = new() { GlobalMenu = false }` keeps it. Flatpak manifests need `--talk-name=com.canonical.AppMenu.Registrar` (manifests that `dotnet runic package flatpak` writes include it) |
| `Validate` reported `menu-bar-unsupported` for Windows and GTK | Only for `Native` windows without a bar, and for each OS's reserved shortcuts (Windows and Linux: Ctrl+Z/Y/X/C/V/A, Ctrl+Shift+Z, Alt+F4, F10) |
| A `Native` decorated GTK window's child was the web view | A vertical `GtkBox`; the web view is its last child |
| Browser accelerators (F5, Ctrl+P, Ctrl+F) on declared keys | The declared item runs unless the page calls `preventDefault()` |

Titles are 1–128 characters without NUL; command ids 1–64 ASCII letters, digits,
`.`, `_` or `-`, not starting with `runic.`. Anything else throws
`ArgumentException`. Views.Desktop menu commands receive the item's
`CommandParameter`; a throwing command target is logged as event 3014.

### Web content and browsers

Embedded windows (WebView2, WPF `RunicWebView`, WebKitGTK, WKWebView) keep the
page on the application. Restore the old behaviour per window:

```csharp
new DesktopWindowOptions
{
    WebContent = new()
    {
        ExternalNavigation = DesktopExternalNavigation.AllowInWindow, // links to other sites stay in the window
        AllowDownloads = true,                                        // downloads were allowed
        DevTools = true,                                              // DevTools outside Development
    },
};
```

- `target="_blank"` and `window.open` no longer open a second WebView; open
  windows from .NET.
- An installed browser launches `<surface>/_runic_launch/<code>`; a cross-site
  `AdditionalOrigins` entry (`localhost` for a `127.0.0.1` surface) gets no
  bootstrap in browser mode: use a same-site origin. The path
  `<surface>/_runic_launch/` is reserved.
- Surfaces send `frame-ancestors 'self'` and `X-Frame-Options: SAMEORIGIN`; a
  surface framed elsewhere gets no Bridge credential. WKWebView needs
  `MediaCapture` for camera and microphone.
- The page's Bridge closes at `pagehide` instead of `beforeunload`.
- Remove `'unsafe-eval'` from your Content-Security-Policy
  (`RunicDesktopHostOptions.ContentSecurityPolicy`) unless a page dependency or
  `ExecuteJavaScriptAsync` evaluates strings.

### Windows, drops and WPF

- A placement saved full-screen restores full-screen; Windows `IsFullScreen` is
  `true` after `SetFullScreenAsync(true)`; a macOS `Kiosk` window enters full
  screen; GTK 4 on X11 applies `X`/`Y`. The connection timeout counts from the
  page's first request.
- Same-origin iframe drops raise `FilesDropped`; macOS promised files arrive in
  `PromisedFiles` with possibly empty `Paths`; GTK reports the negotiated `Effect`.
- `RunicWebView.CreateWindowHostFactory()` is stable (remove `RUNICDESK100`);
  `WpfBridgeView` and `RunicViewHost` use the default web content policy.

### CS-WebUI

- A released View answers kind `released`, "This View was released."; calls are
  no longer serialized behind an admission gate.
- Update any copy of `runic-cswebui.js` kept outside `www/`: an older copy
  receives no state.
- A WebUI library you supply (`CSWEBUI_NATIVE_LIBRARY`) needs `efac0e24dd` or
  later and logs event 2101 once.

Details: 0.10.0 [Runic Desktop and GTK 4](../eng/release/notes/0.10.0.md#runic-desktop-and-gtk-4),
[Desktop and CS-WebUI hosts](../eng/release/notes/0.10.0.md#desktop-and-cs-webui-hosts),
[WPF](../eng/release/notes/0.10.0.md#wpf); 0.11.0
[Upgrading](../eng/release/notes/0.11.0.md#upgrading) and
[Behaviour changes](../eng/release/notes/0.11.0.md#behaviour-changes).

## Platform

### Menu model and status icons

`DesktopMenuItem` changes meaning: it was the abstract base of status icon
entries and is now the sealed command item. A `case DesktopMenuItem` written
against 0.9 still compiles but matches only commands. Keep a default arm: the
`DesktopMenuEntry` union and `DesktopMenuRole` are open.

```csharp
// 0.9
new DesktopMenuCommand("open", "Open window")
// 1.0: the title comes first
new DesktopMenuItem(Title: "Open window", CommandId: "open")
```

| 0.9 | 1.0 |
| --- | --- |
| `DesktopMenuItem` (base), `ImmutableArray<DesktopMenuItem>` in `DesktopStatusIconState.Menu` | `DesktopMenuEntry` |
| `DesktopMenuCommand.Id`, `.Label` | `DesktopMenuItem.CommandId`, `.Title` |
| `new DesktopMenuSubmenu(label, items)` | `new DesktopMenu(label, items)` |
| `activation.MenuItemId`, `new DesktopStatusIconActivation(kind, MenuItemId: …)` | `activation.CommandId`, `CommandId: …` |
| Command ids starting with `runic.` | Rename them (reserved) |

Status icon menus accept a repeated command id with different `CommandParameter`s;
more than 32 activations waiting for a slow `Activated` handler are dropped
(`status-icon-activations-dropped`).

### Launching and services

| 0.9 | 1.0 |
| --- | --- |
| `LaunchUriAsync(new Uri("ms-settings:…"))` threw | Opens Settings on Windows; `Unavailable(BackendUnavailable)` elsewhere. Only `ms-settings:<page>` is allowed |
| `mailto:` URIs with an `attach…` header | `ArgumentException` |
| `LaunchAsync(path, Open)` on scripts, installers, disk images, `.rdp`, `.theme` and similar | `Failed(PermissionDenied)`; use `Reveal` or `ChooseApplication` |
| `snapshot.Statuses["platform.uris.launch"]` and the other literals | `PlatformCapabilityIds.UrisLaunch` and its siblings (same values); code that compares the whole key set adds the new `platform.*` keys |
| WPF `AnnounceAsync` `Unavailable`; Flatpak/Snap secrets without Secret Service permission `Unavailable` | `Success`; stored through the Secret portal |
| Windows saves took the folder's inherited ACL | The replaced file's DACL |

Also newly working: MSIX login startup through the manifest's `StartupTask`,
locked BitLocker and APFS volumes (`IsLocked`, `MountAsync` is `Failed(Locked)`),
Windows optical eject, Linux trash `directorysizes`; UDisks2 `OptionNotPermitted`
is `Failed(InvalidData)`.

### Linux portal and D-Bus

- Notification activations that match no notification the process showed keep
  only `http`, `https` and `mailto` links and are untrusted.
- Portal diagnostics are logged at their `Severity`; some `status-icon-*`,
  `login-startup-*` and `desktop-application-*` codes become warnings.
- Exported D-Bus objects run at most 16 calls with 64 waiting
  (`LimitsExceeded`); override `DBusObject.MaxConcurrentCalls` for calls that
  wait for a user. Values nested deeper than 64 levels are `InvalidArgs`.

### Windows administration

| 0.9 | 1.0 |
| --- | --- |
| `TaskTrigger.StartBoundary`/`EndBoundary` (`string?`) | `DateTime?` (`Kind` selects target clock, UTC or local offset) |
| `ImmutableArray<byte>` security descriptors (`ShareSpecification`, `ShareSnapshot`, `ShareUpdate`) | `WindowsSecurityDescriptor.FromBinaryForm(bytes)` or `.FromSddl("…")`; `.BinaryForm`, `.ToRawSecurityDescriptor()` |
| `FirewallRuleSpecification.Protocol = 6` | `FirewallProtocol.Tcp`; other numbers `(FirewallProtocol)47` |
| `ServiceStatus.NativeAcceptedControls` | `ServiceStatus.AcceptedControls` (`ServiceAcceptedControls`) |
| `ActiveDirectoryClient.Create…Async` returned `Task` | `Task<ActiveDirectoryObjectIdentity>` (existing `await`s compile) |
| Remote WMI by IP address, NTLM fallback | Kerberos only: name the server by DNS or NetBIOS name |
| A firewall update could leave an unscoped inbound allow rule | `ArgumentException` |

Details: 0.10.0 [Platform and Runtime](../eng/release/notes/0.10.0.md#platform-and-runtime),
[Windows administration](../eng/release/notes/0.10.0.md#windows-administration),
[D-Bus](../eng/release/notes/0.10.0.md#d-bus); 0.11.0
[Behaviour changes](../eng/release/notes/0.11.0.md#behaviour-changes).

## Assets

`DevelopmentDirectoryAssetSource` is stable on Linux, Windows and macOS; remove
`RUNICASSETS001` suppressions and `OperatingSystem.IsLinux()` guards.

| 0.9 | 1.0 |
| --- | --- |
| `source.Refresh(ct)` | `await source.RefreshAsync(ct)` |
| `using IAssetWatch watch = source.StartWatching(options, ct);`, `watch.IsWatching` | `source.StartWatching(options);` and dispose the source to stop |
| Archives included `.git` and `.env*` | Left out at any depth (`RAS1002` for such an entry point); source maps warn `RAS1005`. `RunicAssetsDefaultExcludes=false` or `--no-default-excludes` restores the old contents |

Details: 0.10.0 [Assets](../eng/release/notes/0.10.0.md#assets), 0.11.0
[Assets](../eng/release/notes/0.11.0.md#assets).

## npm packages

Every `./experimental` entry is removed and its exports moved to the package
root (TS2307 or `ERR_PACKAGE_PATH_NOT_EXPORTED` otherwise):

```sh
grep -rlE "@runic-artifex/[a-z-]+/experimental" src | xargs sed -i -E 's#(@runic-artifex/[a-z-]+)/experimental#\1#g'
# macOS: sed -i ''
```

| 0.9 | 1.0 |
| --- | --- |
| Telling "unavailable" from "invalid argument" by `BridgeError.message` | `BridgeError.reason` (`"unavailable"`, `"invalid-input"`; open); views-effect `ViewRejected.cause.reason` |
| Mock `failNext(route, { kind: "disconnected" })` | `bridge.disconnect()` |
| Mock or hand-written snapshot replies with `protocol: 2` or none | A `hello` reply with `protocol: 4` and `implementation`; see [Views protocol 4](#views-protocol-4) |
| A custom `IBridgeTransport` answering an unbound route `kind: "disconnected"` | The window's dispatcher answers `kind: "released"`, `message: "This View was released."`; a transport only implements `Serve` and `BroadcastAsync` ([Views protocol 4](#views-protocol-4)) |
| A hand-written `createLatestOperationController` wrapper in a component | `useLatestOperation()` (React, Vue, Svelte), `injectLatestOperation()` (Angular) |
| Generated client `Promise<<Name>Client>` | `Promise<<Name>Client & globalThis.Disposable>`; the `.d.ts` files need TypeScript 5.2 (`esnext.disposable`) |
| Vite `>=8.0.12 <9` | `>=8.3.3 <9` |

An operation start rejected as `unavailable` also sets `BridgeError.reason`, and
under `vite dev` with the plugin's development client the connection is observed
from page load.

Details: 0.10.0 [Views npm packages](../eng/release/notes/0.10.0.md#views-npm-packages-wire-and-generated-clients),
[Framework adapters](../eng/release/notes/0.10.0.md#framework-adapters-sveltekit-and-the-vite-plugin);
0.11.0 [Behaviour changes](../eng/release/notes/0.11.0.md#behaviour-changes).

## CLI tools

| 0.9 | 1.0 |
| --- | --- |
| `--output json` | `--format json` (`--output` stays a hidden alias until 2.0; `Runic.Create --output` is still the destination). Regenerate installed completion scripts |
| Tool-specific exit codes | 0 success, 1 failed or unavailable, 2 usage, 3 internal, 130 cancelled (the administration verifier keeps 0/1/2) |
| `Runic.Create` prompts disabled by redirecting standard output | Prompts on standard error: pass `--yes`, set `RUNIC_COMMANDLINE_NO_INPUT=1` or redirect standard input |
| `dotnet runic dev` with `runic()` in Vite | Needs `@runic-artifex/vite-plugin-runic` 0.11 or later; otherwise readiness times out with `RAPPDEV1007` |
| `dotnet runic doctor` failed on Node 26 | Warns; `--fail-on warn` also fails on the new checks |

An Angular frontend that declares the plugin adds the session proxy to its
`proxyConfig` file:

```js
// proxy.conf.mjs
import { runicDevSessionProxy } from "@runic-artifex/vite-plugin-runic";
export default { ...runicDevSessionProxy() };
```

Details: 0.10.0 [Command-line tools](../eng/release/notes/0.10.0.md#command-line-tools),
0.11.0 [Command-line tools](../eng/release/notes/0.11.0.md#command-line-tools).

## Templates

Existing projects are not rewritten. To match a new `runic-app` project:

- commit `packages.lock.json` (`RestorePackagesWithLockFile`) and the frontend
  lock file;
- drop `'unsafe-eval'` from the policy outside `Development`;
- replace `packaging/package.mjs` with `dotnet runic package` (`--packaging`
  copies its manifests as editable overrides; see the [packaging guide](packaging.md));
- for Angular, add `runicDevSessionProxy()` as above.

Projects created from the 0.9.0 template also take the 0.9.1 frontend
dependency updates ([0.9.1 notes](../eng/release/notes/0.9.1.md)). Details: 0.11.0
[Templates](../eng/release/notes/0.11.0.md#templates).

## From 0.11 to 0.12

0.12.0 changes behaviour on macOS, the supported macOS architectures and the
layout of macOS packages. No public API is removed, so code that compiled
against 0.11 compiles unchanged; outside macOS only file saves change. The
tables also apply to an application coming from 0.9.

### macOS quit and lifecycle

Every quit takes one path, also with no window open: Command and Q, Dock › Quit,
a script's quit Apple event, and logout, restart or shutdown. Runic asks
`DesktopHostOptions.ConfirmQuitAsync`, then each window's close guard in turn,
then cancels `host.ShutdownRequested` and closes the remaining windows; the quit
completes when the application's code returns. A refusal keeps the application
running and cancels a logout.

| 0.11 | 0.12 |
| --- | --- |
| Command and Q asked each window to close | `ConfirmQuitAsync`, the close guards, then `ShutdownRequested` is cancelled |
| Dock › Quit, scripts and logout ended the process from AppKit, skipping close guards and `ShutdownRequested` | The same path as Command and Q; they can be refused |
| A quit with no window open did nothing | `ShutdownRequested` is cancelled. Without a surface and without `HandlesShutdownWithoutWindow`, the process is then terminated |
| Save prompts in close guards only, never on logout | `ConfirmQuitAsync`, which also runs on logout, and on Ctrl+C, SIGTERM and SIGHUP when set (`DesktopQuitSource.Signal`, every OS) |
| A Dock click with no window did nothing | `DesktopHost.ReopenRequested` (`HasVisibleWindows`) |
| No Dock menu | `DesktopHostOptions.MacOS.DockMenu`, run through `host.MenuCommands` |
| `NSApp.delegate = …` | `DesktopHostOptions.MacOS.ForwardingApplicationDelegate`; Runic's delegate forwards what it does not handle. Setting `NSApp.delegate` loses open events, universal links, quitting, reopening and the Dock menu |

An application that keeps running behind a status icon, or that asks about
unsaved documents, moves to:

```csharp
var options = new DesktopHostOptions
{
    // 0.11: only the close guards asked, and never on Dock › Quit or logout.
    ConfirmQuitAsync = async (request, cancellationToken) =>
        !documents.HasUnsavedChanges || await documents.SaveAllOrAskAsync(request.Source, cancellationToken),
    // Keep running with no window open, and end on ShutdownRequested.
    HandlesShutdownWithoutWindow = true,
    // 0.11: NSApp.delegate = myDelegate (an Objective-C object).
    MacOS = new MacOSDesktopOptions { ForwardingApplicationDelegate = myDelegate },
};
return DesktopEventLoop.Run(options, async host =>
{
    host.ReopenRequested += (_, e) => { if (!e.HasVisibleWindows) OpenMainWindow(); };
    await RunUntilAsync(host.ShutdownRequested);
    return 0;
});
```

`DesktopEventLoop.Run` now runs AppKit's own loop (`[NSApp run]`) with Runic's
delegate instead of polling every 5 ms:

| 0.11 | 0.12 |
| --- | --- |
| Native callbacks and Runic's main-thread work ran between polls, in the default run loop mode only | In every mode, also while a menu tracks, a window resizes live or a modal panel runs: queued work can run while a menu or panel is open |
| `MacOSPlatformProvider.CreateOpenRequests` on the main thread before the first window or status icon, else `InvalidOperationException` | Under `DesktopEventLoop.Run`, on any thread at any time; open events that arrive earlier are kept (up to 64) and delivered in order. The launch's own open event arrives after the launch's arguments |
| `Run(Func<Task>)` created `NSApp` only when a service needed it | Always creates it on macOS |
| AppKit calls without a window needed a window's `InvokeNativeAsync` | `DesktopEventLoop.InvokeOnMainThreadAsync` |

### macOS window coordinates

Positions on macOS are points measured right and down from the primary
screen's top-left corner, as on Windows and Linux. The primary screen is the one
with the menu bar.

| 0.11 | 0.12 |
| --- | --- |
| `MoveAsync(x, y)`, `DesktopWindowOptions.X`/`Y`: the frame's bottom-left corner, measured up from the primary screen's bottom | The frame's top-left corner, measured down from the primary screen's top. `MoveAsync(workArea.X, workArea.Y)` puts the title bar below the menu bar |
| `DesktopWindowState.X`/`Y` and `Placement`: bottom-left, no `Origin` | Top-left, `Placement.Origin = DesktopCoordinateOrigin.TopLeft` |
| `DesktopMonitor.Bounds`/`WorkArea`: `Y` is the bottom edge | `Y` is the top edge; a monitor above the primary has a negative `Y`; the primary's `WorkArea.Y` is the menu bar's height |
| Saved placements | Nothing to do: a placement without `Origin` (`null`) opens where it did and is reported top-left from then on |
| `ResizeAsync` kept the bottom-left corner; the window grew upward | It keeps the top-left corner |

Positions the application computed itself, for example from `NSScreen` frames,
are flipped once, or marked as bottom-left so that Runic converts them:

```csharp
// 0.11: AppKit's bottom-left corner of the frame.
await window.MoveAsync(frame.X, frame.Y);
// 0.12: the top-left corner, measured down from the primary screen's top.
await window.MoveAsync(frame.X, primaryScreenHeight - (frame.Y + frame.Height));

// Or keep a stored bottom-left position and let Runic convert it when the window opens.
var placement = stored with { Origin = DesktopCoordinateOrigin.BottomLeft };
```

Windows and Linux ignore `Origin`; every built-in host writes `TopLeft`
(`"Origin":0` in JSON), which 0.11 readers ignore.

### macOS architectures and packaging

| 0.11 | 0.12 |
| --- | --- |
| `osx-x64` (Intel) publishes and packages | Not a Runic target: publish for `osx-arm64`. `dotnet runic package` refuses `osx-x64` with `RAPPPKG1004`, and `dotnet runic doctor --runtime osx-x64` fails `target-rid` |
| `dotnet runic package macos --runtime osx-arm64,osx-x64` built a universal bundle | Refused with `RAPPPKG1004`: every target takes one runtime identifier |
| Every published file in `Contents/MacOS` | Only Mach-O files; the others move to `Contents/Resources` with a symbolic link left in `Contents/MacOS`. `AppContext.BaseDirectory` lookups work unchanged; code that enumerates `Contents/MacOS` sees links |
| `.txt`, `.md`, `.png` and other system types exported as `<ApplicationId>.<ext>` with rank `Owner` | The system type with rank `Alternate`. Set `RunicMacFileTypeHandlerRank=Default` to ask to be the default handler, and `RunicMacFileTypeIdentifiers` (`.log=public.log`) for a type the built-in list does not know. Your own extensions stay exported with `Owner` |
| Nested libraries and executables left unsigned, or signing failed | Each is signed with the hardened runtime, deepest first; the bundle is verified with `codesign --verify --deep --strict` |
| No camera or microphone usage strings | `RunicMacCameraUsageDescription`, `RunicMacMicrophoneUsageDescription`; without them a page's `getUserMedia` fails with `NotAllowedError`, even with `MediaCapture` |
| No sandbox, no `.pkg` | Opt in with `--sandbox` (`RunicMacSandbox`), `--provisioning-profile`, `--pkg`, `--installer-sign` and `RunicMacApplicationCategory` |

An `Info.plist` or `entitlements.plist` override in `packaging/macos/` takes the
new fields where the project uses the matching options; an `Info.plist` override
without `{{UsageDescriptions}}` fails with `RAPPPKG1006` once a usage
description is set, as does an `entitlements.plist` override without
`{{Entitlements}}` that lacks a key `--sandbox` or `--provisioning-profile`
needs:

```xml
<!-- packaging/macos/Info.plist -->
  <key>NSHighResolutionCapable</key><true/>{{ApplicationCategory}}{{UrlSchemes}}{{DocumentTypes}}{{UsageDescriptions}}

<!-- packaging/macos/entitlements.plist -->
<dict>{{Entitlements}}
```

```xml
<PropertyGroup>
  <RunicMacCameraUsageDescription>Video calls show your camera.</RunicMacCameraUsageDescription>
  <RunicMacFileTypeHandlerRank>Default</RunicMacFileTypeHandlerRank>
</PropertyGroup>
```

A sandboxed application that sets `AllowDownloads` adds
`com.apple.security.files.downloads.read-write` to its entitlements override.

### macOS development bundle

`dotnet run`, `dotnet watch` and `dotnet runic dev` on macOS start
`obj/…/runic-macos/<AssemblyName>.app/Contents/MacOS/<AssemblyName>`, an ad-hoc
signed bundle, instead of `bin/…/<AssemblyName>`. Notifications, login startup,
open registration and the `Info.plist` URL schemes and file types work as in a
packaged application, and macOS keeps one set of permissions for the
development bundle and the shipped application. `AppContext.BaseDirectory` is
still the build output. To run the build output directly, as in 0.11:

```xml
<PropertyGroup>
  <RunicMacDevelopmentBundle>false</RunicMacDevelopmentBundle>
</PropertyGroup>
```

Launch profiles with `commandName: Executable` and IDEs that start the output
directly are unaffected.

### Saves and downloads

| 0.11 | 0.12 |
| --- | --- |
| Windows: replacing a file another program holds open without delete sharing was `CommitUnknown(IOError)` | `NotCommitted(ResourceBusy)`: the file was not replaced; try again later |
| A second `BeginWriteAsync` on a save lease was `Failed(ResourceBusy)` | Repeatable: a new write begins once the previous commit finished or its transaction was disposed; `Failed(ResourceBusy)` only while one is open |
| A commit compared the file with the selection | With the content the application last saw: the last committed write, or a document's last `OpenReadAsync`. After `NotCommitted(Conflict)`, read with `OpenReadAsync` and write again to overwrite |
| macOS App Sandbox: `platform.files.save` unavailable, `BeginWriteAsync` `Unavailable(AtomicReplaceUnavailable)` | Available: selections save through item replacement |
| A document editor kept the path and reopened it with `OpenFileAsync` and `SaveFileAsync` | `IFileDialogs.OpenDocumentAsync`/`SaveDocumentAsync` return one `IDocumentFileLease` that reads and saves repeatedly; `CreateBookmarkAsync` and `IDesktopFileBookmarks.OpenDocumentAsync` keep recent files |
| macOS window with downloads refused: an attachment WebKit can show was shown in the window | Cancelled, as on Linux. With `AllowDownloads` the file goes to Downloads, as in the other embedded windows |

A save loop that treated the second `BeginWriteAsync` as the end of the lease,
or `CommitUnknown(IOError)` on Windows as "maybe written", checks the new
outcomes:

```csharp
switch (await write.CommitAsync())
{
    case FileCommitResult.NotCommitted { Code: PlatformFailureCode.ResourceBusy }:
        // Windows: another program holds the file open; nothing was written.
        break;
    case FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }:
        // Another program changed the file: reload, or read and write again to overwrite.
        break;
}
```

Details: 0.12.0 [Upgrading](../eng/release/notes/0.12.0.md#upgrading),
[Behaviour changes](../eng/release/notes/0.12.0.md#behaviour-changes) and
[Breaking changes](../eng/release/notes/0.12.0.md#breaking-changes).

## From 0.12 to 0.13

0.13.0 replaces Views protocol 3 with protocol 4, the protocol that 1.0 keeps,
and turns on spell checking in Linux windows. Generated code changes with the
protocol; application code changes only where it implements or calls the host
bridge or a transport itself, uses the untyped mock or `InMemoryBridgeTransport`
directly, or reads `TimeSpan` text. The tables also apply to an application
coming from 0.9.

### Views protocol 4

Update every Runic NuGet and npm package to 0.13.0 together, rebuild so the
generated clients, bridges and mocks are regenerated, and reload open pages. A
page and a host of different protocol versions do not connect: a cached 0.12 page
reports a `protocol` diagnostic ("The .NET host uses Views protocol 4, but this
client implements protocol 3 …") and its calls fail.

| 0.12 | 0.13 |
| --- | --- |
| Views protocol 3: one host route per member (`counterSetStep`), a `Snapshot` and `Mount`/`Unmount` per View, `__{route}Changed` pushes, long-poll routes for operations, interactions and the menu bar | Protocol 4: one request endpoint and one push channel per connection, a `hello` handshake, `open`/`close`, frames with member changes, replies that name a revision, `watch` pushes. Nothing to change in code that uses generated clients |
| `TimeSpan` values as `"1.02:03:04.5000000"` (.NET `c` format) | ISO 8601 durations, `"P1DT2H3M4.5S"`, `"PT0S"`, `"-PT1.5S"`. The TypeScript type stays `string`: parse it with `Temporal.Duration.from` or your own code, and update stored or compared strings |
| A custom `IBridgeTransport` with `Bind`/`BindAsync`/`Publish`, `IAsyncBridgeTransport` or `IBridgeUtf8Transport` | `Serve(IBridgeRequestHandler)` and `BroadcastAsync`, plus `IBridgeConnectionTransport.SendAsync` if the transport can address one connection; report closed connections with `ConnectionClosed`. See [Custom window hosts](../packages/dotnet/Runic.Application.Testing/README.md#custom-window-hosts) |
| Code that reads `IBridgeArguments` | Removed; generated descriptors read the request's JSON value |
| A custom `__runicBridge` or a test double with `call(name, …args)` and `onReconnect` | `request(message): Promise<string>`, `onMessage(listener)` and the now required `onConnectionChange(listener)` ([Host bridge](../specs/application/README.md#host-bridge-page)) |
| A copy of `runic-desktop-views.js` or `runic-cswebui.js` kept outside the web content | Take the 0.13 copy; a 0.12 copy speaks protocol 3 |
| `snapshot` changed only after .NET confirmed a setter | It shows the setter's value at once, until the state that includes the edit arrives. A failed setter rejects and the member returns to .NET's value. Await the setter's promise where the page needs .NET's confirmation, such as a "Saved" indicator |
| A text input bound to state with `(input)="client.setName(…)"`, or a command per key | It keeps working with a setter. Prefer `injectTextField` ([Angular](../packages/web/angular/README.md#text-inputs)) or `useTextField` ([React](../packages/web/react/README.md#text-inputs-usetextfieldview-read-write-options), [Vue](../packages/web/vue/README.md#text-inputs-usetextfieldview-read-write-options), [Svelte](../packages/web/svelte/README.md#text-inputs-usetextfieldview-read-write-options)) for coalescing and keeping the draft after an error, a setter rather than a command per key, and don't disable the field on its own busy state ([Text inputs](../packages/web/views/README.md#text-inputs)) |
| `BridgeFailureDetail.type` is `string` | `string \| undefined` |
| `BridgeError.route` such as `counterIncrement` | The View route, such as `counter` |
| `BridgeOperationUncertainError.contract` `{ViewModel}:{fingerprint}:{route prefix}` | `{view}:{fingerprint}`; `route` is separate |
| An operation started by 0.12 and recovered after the upgrade | `unknown`: every contract fingerprint changes (`runic-bridge-contract-v5`) |
| The `version` diagnostic for any producer | Only for `Runic.Application.Views`; another producer is identified by `implementation` in `hello` |
| Telemetry `runic.bridge.route` holding a host route such as `editorSetTitle` | The View route (`editor`), with the member in `runic.bridge.member`; `runic.bridge.frame` is `state`, `frame` or `failure` |
| Content of a parent View presented at the parent's first change | Presented once a client read the parent's state (`open` or `snapshot`): a route publishes nothing before its first state |
| A page that calls the menu bar routes (`__runicMenuBarWatch`, `__runicMenuBarInvoke`, `__runicMenuBarWait`) itself | Menu bar page protocol 2: requests to the reserved route `runic.menu-bar` and `menu-bar` pushes ([menu-bar-page.md](../specs/desktop/menu-bar-page.md)); `createMenuBarController` does this |
| Code that compared `BridgeWire.EncodeCanonical` output with stored strings | RFC 8785 (JCS): numbers as IEEE doubles written as ECMAScript does, minimal string escaping. `0.1` and `0.10000000000000000001` are one value; `1e400` and invalid Unicode throw `FormatException` |

Generated TypeScript (`@runic-artifex/views/generated`, `/generated/wire`) and
generated C# descriptors change shape; regenerate rather than edit them. The
wire decoders `guid`, `dateOnly`, `timeOnly` and `dateTimeOffset` are now
`uuid`, `localDate`, `localTime` and `offsetDateTime`, and `float32` is new.

### Mocks and tests

The untyped mock (`@runic-artifex/views/mock`) answers protocol-4 requests,
addressed by request kind and member wire name instead of route suffixes.
Generated typed mocks (`mock<Name>(bridge, definition)`) keep their definitions.

| 0.12 | 0.13 |
| --- | --- |
| `bridge.view("counter", { routes: { Increment: state => … } })` | `bridge.view("counter", { commands: { increment: state => … } })` |
| `routes: { SetName: (state, value) => … }` | `set: { name: (state, value) => … }`; without a handler a `set` applies the value |
| `routes: { CanIncrement: () => false }` | `can: { increment: () => false }`, or `canIncrement: false` in the state |
| `bridge.route(name, handler)` for any other route | `bridge.intercept(request => reply \| undefined)` and `bridge.push(message)` |
| `bridge.failNext("counterIncrement", failure)` | `bridge.failNext({ route: "counter", kind: "command", member: "increment" }, failure)`, or `view.failNext({ kind, member }, …)`; typed mocks keep `failNext(method, …)` |
| `bridge.calls` with route names such as `editorSave` | `MockCall { kind, route, member, message }` with wire member names: `{ kind: "command", route: "editor", member: "save" }`. Typed mocks' `calls` hold the same objects |
| Typing tests against an instant mock | `createMockBridge({ latency: "slow", reorderRequests: true })` and key-by-key typing ([Typing in tests](../packages/web/views/README.md#typing-in-tests)) |
| `InMemoryBridgeTransport.Call("counterSetStep", new BridgeTestArguments(Int64Value: 5))`, `CallAsync`, `Routes` | `host.Root.Set(vm => vm.Step, 5)` and the other `RunicViewDriver` calls, or `transport.Request("""{"kind":"set","route":"counter","member":"step","value":5,"edit":1}""")` after a `hello` |
| `InMemoryBridgeTransport.TakePublications()`, `RunicPublicationKind.Delta` | `TakeMessages()` (`RunicBridgeMessage(Json, ConnectionKey)`) on the transport, the drivers' `TakePublications()`; `RunicPublicationKind.Frame` with `Sets`, `Texts` and `Fields` |
| `RunicViewDriver.Mount(token)`/`Unmount(token)` | `Mount(presentation)`/`Unmount(presentation)` send `open`/`close` |
| `RunicCallReply.State` from the reply | Read with a `snapshot` after the reply, which carries `Revision`, `Released` and `Superseded` |

### Spell checking and windows

| 0.12 | 0.13 |
| --- | --- |
| Linux (GTK 3 and GTK 4) windows never checked spelling | They check `spellcheck` fields by default, as WebView2 and WKWebView did. Set `DesktopWindowOptions.SpellChecking = false` for the 0.12 behaviour, for example in an editor that draws its own marks |
| Spelling languages: the platform's choice | `DesktopHostOptions.SpellCheckingLanguages` (BCP 47, for the whole application) and `DesktopHost.SetSpellCheckingLanguagesAsync`. Linux uses enchant dictionaries, falling back to the user's locale variables; macOS sets `NSSpellChecker`'s language for one language and identifies the language automatically for several; Windows ignores them (`window-option-unsupported`, WebView2 uses `DesktopWindowOptions.Language`). A language without a dictionary is reported as `spell-checking-dictionary-missing` |
| A Nix-packaged application had no dictionaries | `buildRunicApplication { spellingDictionaries = with pkgs.hunspellDicts; [ en_US de_CH ]; }` (`desktop` and `desktop-gtk4` hosts) |
| GTK 4 on X11: a restored `DesktopWindowPlacement` opened where the window manager placed it, a maximized one restored there, and KWin positions drifted by the frame | The window opens at the saved position, a maximized placement restores to its saved bounds, and the reported position is the frame's top-left corner. Saved placements need nothing. On Wayland the position is still ignored |

Details: 0.13.0 [Breaking changes](../eng/release/notes/0.13.0.md#breaking-changes),
[Behaviour changes](../eng/release/notes/0.13.0.md#behaviour-changes) and
[Added](../eng/release/notes/0.13.0.md#added).
