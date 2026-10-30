# Upgrading to 1.0

This guide collects what an application written against Runic SDK 0.9.x changes
to run on 1.0. An application still on a 0.7 or 0.8 preview starts with
[From 0.7/0.8 previews](#from-0708-previews), which lists what 0.7.0-preview.7 to
0.9.0 removed and renamed, and then follows the rest of the guide. 0.10.0 made
every breaking change planned before 1.0; 0.11.0 only
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
- 0.14.0: [Fork integration](../eng/release/notes/0.14.0.md#fork-integration-validation-and-dynamicdata-v1),
  [Text splices and requests with `hello`](../eng/release/notes/0.14.0.md#text-splices-and-requests-with-hello-p1)
- 0.15.0: [Views protocol](../eng/release/notes/0.15.0.md#views-protocol-v5)

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

Compatible versions: CsWebUi 2.5.0-beta.4.8 or later, Vite `>=8.3.3 <9`,
TypeScript 5.2 or later, Node `^24.18.0 || ^26.0.0` (Node 24 is the certified
version). The SDK tools (`dotnet-runic`, `Runic.Create` and the asset packer
that `Runic.Assets` runs during the build) are built with Runic Command Line
0.11.0 (since 0.14; 0.9.1 before) and carry their own copy; no SDK library
references Command Line, so an application may use any Command Line version
itself (0.11.0 is current).

The toolchain floor is .NET SDK 10.0.403 or later (runtime 10.0.14) and, for a
Bun frontend, Bun 1.4.4 or later, each within its major version (0.12.1); for
an npm or pnpm frontend, `dotnet runic doctor` also wants Node 24.22.1 or a
later Node 24, or Node 26. On an older SDK or Bun, such as SDK 10.0.401 or Bun
1.4.2, `dotnet runic doctor` fails its `dotnet-sdk`, `javascript-runtime` or
`package-manager` check, and so exits 1 under the default `--fail-on fail`.
Since 0.14 the build warns too
([`RUNICBRIDGE033`](diagnostics.md#runicbridge033)): for the SDK on every build,
for Bun when it builds a Bun frontend. Under Nix, `buildRunicApplication`
builds with your nixpkgs' SDK and Bun, which may still be older.

`Runic.Desktop` now brings `Runic.Platform` and `Runic.Platform.Linux.DBus` at its
own exact version, and Tmds.DBus.Protocol, on every OS.
On macOS, Runic supports macOS 15 or later on Apple silicon (`osx-arm64`) only;
see [From 0.11 to 0.12](#from-011-to-012).

## From 0.7/0.8 previews

An application on a 0.7 or 0.8 preview makes these changes first, then the
rest of this guide. Rebuild after updating: generated Bridges, TypeScript
clients and mocks change. Most old names were removed rather than deprecated,
so code that uses them no longer compiles. The tables name each type or
package; member renames in the same areas are in the 0.9.0
[Upgrading](../eng/release/notes/0.9.0.md#upgrading) tables, and the Window
and build property renames of 0.7.0-preview.7 in
[Moved and renamed since 0.7.0-preview.6](../packages/dotnet/Runic.Application.Views/README.md#moved-and-renamed-since-070-preview6).

The old package IDs end at 0.7.0-preview.6; changing only the version fails
restore with `NU1101` or `NU1102`. Namespaces are unchanged (0.7.0-preview.7):

| Old package | New package |
| --- | --- |
| `Runic.Application` | `Runic.Application.Views` |
| `Runic.Application.CsWebUi` | `Runic.Application.Views.CsWebUi` |
| `Runic.Application.Desktop` | `Runic.Application.Views.Desktop` |
| `Runic.Application.Wpf` | `Runic.Application.Views.Wpf` |
| `Runic.Application.ReactiveUI` | `Runic.Application.Views.ReactiveUI` |
| `Runic.Application.ReactiveUI.Reactive` | `Runic.Application.Views.ReactiveUI.Reactive` |

XAML that names `assembly=Runic.Application.Wpf` names
`Runic.Application.Views.Wpf`, or uses the `rv` namespace
`https://runic-artifex.eu/xaml/views` (0.7.0-preview.8).

Since 0.14, the removed types that the table marks 0.9.0 and that
[Removed types](#removed-types) lists, such as `RunicReactiveSchedulerProvider`,
`INativePickerOwner` and `RunicBridgeJsonIgnore`, fail with `CS0619` naming
their replacement instead of `CS0246`, for one release.

| 0.7/0.8 | 1.0 | Release |
| --- | --- | --- |
| `RunicBridge*` build properties, `RunicViewsWindowProject`, `RunicBridgeFrontendInput` items | `RunicApplicationFrontend*` names; an old name fails with `RUNICBRIDGE017`, which names the new one | 0.7.0-preview.7; errors since 0.9.0 |
| `RunicApplicationId`, `RunicApplicationName` | `ApplicationId`, `ApplicationTitle` (`RUNICBRIDGE017`); macOS and Polkit read `ApplicationId` too | 0.9.0 |
| `<ProjectName>.RunicBridgeComposition` | `<RootNamespace>.RunicViewsComposition`; set `RunicApplicationFrontendCompositionType` to keep the old name | 0.9.0 |
| `RunicApplicationFrontendPackageDirectory`, `RunicApplicationFrontendViteDevServerEnabled=true`, `RunicApplicationHostAdapter` | Removed (the frontend directory is the package directory), `RunicApplicationFrontendDevServerKind=vite`, internal (set `RunicApplicationFrontendGenerationEnabled=true`) | 0.9.0 |
| Privileged actions: `RunicPolkitPolicy`, `RunicMacLaunchDaemonPlist`, `RunicElevatedHelper`; `RunicPrivilegedAction` metadata `AllowAny`, `AllowInactive`, `AllowActive`, `AllowGui`, `IconName` | `RunicPolkitPolicyCopyEnabled`, `RunicMacLaunchDaemonPlistCopyEnabled`, `RunicWindowsElevatedHelper`; `PolkitAllowAny`, `PolkitAllowInactive`, `PolkitAllowActive`, `PolkitAllowGui`, `PolkitIconName`. The old names fail with `RUNICPA108` | 0.9.0 |
| D-Bus `AdditionalFiles` metadata `Namespace="…"` | `RunicDBusNamespace="…"` (the old name works until 2.0) | 0.9.0 |
| `CsWebUiWindow<T>`, `ReactiveRunicWindow<T>`, `provider.OpenWindow<…>`, `OpenDesktopWindowAsync`, `WebUiApplication.Wait()` | `RunicWindow<T>`, opened with `host.OpenWindowAsync<TWindow>()` inside `RunicDesktopHost.Run` or `RunicCsWebUiHost.Run` | 0.7.0-preview.7 |
| `CsWebUiConfigurationException`; CS-WebUI's `WebUiException` when a window cannot be shown | `RunicWindowConfigurationException` (`Diagnostics`) | 0.7.0-preview.7, 0.8.0-preview.1 |
| `PlatformServices.ForWindow(…)`, `.CreateStatusIcon()`, `.PresentAsync(…)` and the other members | `DesktopPlatformServices.…` (`Runic.Application.Views.Desktop`) | 0.9.0 |
| `new RunicDesktopHost(provider, desktop) { WindowOptions = …, MenuCommands = … }`, `new RunicCsWebUiHost(provider) { UseWebView = true }` | `RunicDesktopHostOptions` or `RunicCsWebUiHostOptions` passed to `Run` or the constructor; `using var menu = host.UseApplicationMenu(commands);` | 0.9.0 |
| `INativePickerOwner` | `INativeWindowOwner` (`Runic.Platform.Runtime`); for WPF dialogs, `WpfPlatformServices.ForWindow(window)` instead of an owner of your own | 0.9.0 |
| `IUiDispatcher`, `PresentationDispatcher` | Removed; run window work through `DesktopNativeOwner.InvokeAsync` | 0.9.0 |
| `PortalPlatformProvider`, the owner-scoped `Create…(owner)` factories of the platform providers, `Gtk3PortalWindowOwner`, `Gtk4PortalWindowOwner` | `new PortalApplication(applicationId, sink).Create…()`; window services from `DesktopPlatformServices.ForWindow(window)` or `AddRunicPlatformServices()`; the host parents portal requests | 0.9.0 |
| `DesktopPlatform.OpenUrl(url)`, `OpenUriAsync(owner, uri)` | `await launcher.LaunchUriAsync(new Uri(url))` on the window's `IDesktopFileLauncher`; without a window, `DesktopPlatformServices.ForApplication().FileLauncher.LaunchUriAsync(uri)` or `IApplicationPlatformServices` (since 0.14) | 0.9.0 |
| `PlatformFailureCode.IoError`; stored `PlatformFailureCode` numbers | `IOError` for I/O failures, `Other` for the rest, and a default arm; add 1 to each stored number | 0.9.0 |
| `new DesktopMenuCommand("open", "Open window")` | `new DesktopMenuItem(Title: "Open window", CommandId: "open")`. **The arguments swap:** both are strings, so the old order still compiles. The item then shows the command id as its title and runs a command named after the title, or the menu is refused with `ArgumentException` when that title is not a valid command id. Name the arguments. See [Menu model and status icons](#menu-model-and-status-icons) | 0.10.0 |
| `session.BeginCloseAsync(timeout)`, `RunicWindowTestHost.BeginCloseAsync(timeout)` | `DrainAsync(timeout, cancellationToken)`; the token stops only the wait | 0.9.0 |
| `new RunicReactiveSchedulerProvider().For(context)`, `RunicReactiveSchedulerProvider.InstallMainThreadScheduler(…)`, `IRunicReactiveSchedulerProvider` | `ReactiveSchedulers.For(context)`, `ReactiveSchedulers.InstallMainThreadScheduler(…)`, `IReactiveSchedulerProvider` (`Runic.Application.Views.ReactiveUI` or `.Reactive`) | 0.9.0 |
| `using Runic.Navigation.ReactiveUI;` for `AddRunicReactiveModelContext()` | `using Runic.Application.Views.ReactiveUI;` (or `.Reactive`); reference `Runic.Navigation.ReactiveUI` yourself for region observables and `CreateBackCommand` | 0.7.0-preview.7 |
| `[assembly: RunicBridgeJsonIgnore]` | Delete it. DTO members with an unconditional `[JsonIgnore]` (condition `Always` or none) or `[JsonExtensionData]` are left out of the Bridge contract by default; `[assembly: RunicIncludeJsonIgnored]` in the ViewModel's assembly keeps them | 0.9.0 |
| `BridgeDiagnostics.IncludeFailureDetail = x` | `RunicWindowOptions.IncludeFailureDetail` per window, or the host options' default | 0.9.0 |
| `RunicBridgeHotReload`, `[RunicBridgeCodec]`, `IRunicBridgeCodec<T>`, `BridgeWindowCloseResult`, `BridgeValidationMessage`, `BridgeCommandResultCardinality` | `RunicViewsHotReload` (`(sender, e)` handler), `[RunicCodec]`, `IRunicCodec<T>`, `RunicWindowCloseResult`, `RunicValidationMessage`, `RunicCommandResultCardinality` | 0.9.0 |
| `ViewModelBridge<T>`, `PropertyDescriptor<T>`, `CommandDescriptor<T>`, `BridgeWire` in hand-written code | `Runic.Application.Views.Generated`, for generated code only; rebuild | 0.9.0 |
| `RunicInteractionInvocation.Enter(…)` | Removed; work outside a Bridge invocation uses a .NET handler | 0.9.0 |
| Routes bound by hand with `IBridgeTransport.Bind`/`BindAsync` and called from the page with `__runicBridge.call(name, …)` | No custom routes: expose a ViewModel command, operation or property and call it through the generated client. A custom transport: see [Views protocol 4](#views-protocol-4) | 0.13.0 |
| `INavigationActivated`, `OnActivatedAsync`, `INavigationRegionGuard.CanLeaveAsync`, `NavigationResume`, `INavigationRegion`, `INavigationEntry` | `INavigationArrived`, `OnArrivedAsync`, `CanDepartAsync`, `NavigationArrival`, `NavigationRegion`, `NavigationEntry` | 0.9.0 |
| `ReactiveRoutedRegion<T>`; `RunicNavigator.DiagnosticId` and `RUNICNAV001` suppressions | `NavigationRegion<TContent>`; remove them (navigation is stable) | 0.9.0; 0.7.0-preview.7 |
| `InMemoryViewTransport`, `ViewTestArguments`, `host.Transport.DrainPublications()` | `InMemoryBridgeTransport`; `TakeMessages()` (`TakePublications()` from 0.9 to 0.12); `RunicViewDriver` calls instead of arguments. See [Mocks and tests](#mocks-and-tests) | 0.9.0, 0.13.0 |
| `reply.Ok`, `reply.EnsureOk()`, `host.Snapshot()` | `reply.Succeeded`, `reply.ThrowIfFailed()`, `host.Root.Snapshot()` | 0.9.0 |
| `IAssetSnapshotSource`, `IAssetManifestProvider`; `OpenSnapshotAsync`, `OpenReadAsync` | `IAssetSource`; `OpenAsync` | 0.9.0 |
| The five `IWindows…Client` interfaces of `Runic.Platform.Administration.Windows`; synchronous service, share and domain calls | Removed (wrap the sealed clients for test fakes); `…Async(…, cancellationToken)` | 0.9.0 |
| `new DBusSignal(…)`, `new DBusPropertyChanges(…)` | Created only by the connection; `DBusProxy.ReadPropertyChanges(signal)` | 0.9.0 |
| `import … from "@runic-artifex/svelte/views"` | `from "@runic-artifex/svelte"` | 0.9.0 |
| `@runic-artifex/vite-plugin-runic/virtual` references, named imports from `virtual:runic/client`, `reportRunicState`, `preserveRunicHmrResource` | Removed; `import "@runic-artifex/vite-plugin-runic/client";` has no exports | 0.9.0 |
| `mockTypedView` from `@runic-artifex/views/mock`; `isViewClient`, `viewSourceIdentity` from `@runic-artifex/views` | `@runic-artifex/views/generated/mock` (rebuilding regenerates `*.mock.ts`); `@runic-artifex/views/bindings` | 0.9.0 |
| Core types (`ViewSource`, `ViewClient`, `CollectionViewport`, …) from `@runic-artifex/react`, `vue`, `svelte` or `angular` | `from "@runic-artifex/views"` | 0.9.0 |
| `runicSpaPageOptions` from `@runic-artifex/sveltekit` | `from "@runic-artifex/sveltekit/page-options"` | 0.9.0 |
| `RunicViewOutlet` (`@runic-artifex/angular`) | `ViewOutlet`; the old name is a deprecated alias, removed in the next major release | 0.7.0-preview.7 |
| `BridgeErrorKind` `disconnected` for a released View; views-effect `ViewDisconnected` for it | `released`; `ViewReleased` | 0.8.0-preview.6 |
| `ViewReference`, `"timedOut"`, `client.startSaveWithRequestId(id, input)`, `waitForBridge(5000)`, views-effect `ViewBridgeTimeout`, `ViewCommandFailed` | `PageReference`, `"timed-out"`, `client.startSave(input, { requestId: id })`, `waitForBridge({ timeout: 5000 })`, `ViewTimeout`, `ViewFailed` | 0.9.0 |
| `dotnet runic … --output json`, `Runic.Assets.Packer … --output json`; tool-specific exit codes | `--format json`; 0, 1, 2, 3 and 130 as below. See [CLI tools](#cli-tools) | 0.9.0 |
| `dotnet runic support`, `RAPPSUP` codes | `runic-translations support` (Runic Translations), `RTRSUP` codes | 0.9.0 |
| `runic-create create MyApp`, `--directory`; template `--host desktop-gtk4` | `runic-create MyApp`, `--output`/`-o`; `--host desktop --linux-gtk 4` | 0.9.0 |

`RUNICPLAT001` (since 0.14) warns about a `DesktopMenuItem` whose title looks
like a command id and whose command id looks like a title, and its code fix
swaps and names the arguments; see
[Menu model and status icons](#menu-model-and-status-icons).

An application that parses its own command line with Runic Command Line meets
these changes in Command Line 0.8.0
([notes](https://github.com/Runic-Artifex/runic-cli-sdk/blob/v0.8.0/eng/release/notes/0.8.0.md#upgrading)).
The SDK tools made the same change in 0.9.0:

| Command Line 0.7 | Command Line 0.8 and later |
| --- | --- |
| `tool --output json` | `tool --format json`. `OutputSelector("--format", "--output")` on the catalog keeps `--output` as a hidden alias; `OutputSelector("--output")` keeps the old spelling. A command option named `--format` is `RCLI0026` |
| Exit codes 3 validation, 4 cancelled, 5 unavailable, 10 command failure, 70 host failure | 0 success; 1 expected failure, including unavailable and command failure; 2 usage and validation errors; 3 unexpected (host) failure; 130 cancelled. A repeated termination signal still exits with 128 plus the signal number. Scripts that test the old numbers test the new ones, or read the JSON fault code where they must tell categories apart |

Command Line 0.11.0 does not accept `--output` by default either, but a catalog
without it answers `--output json` with `Did you mean '--format'?`. An
application whose commands have their own `--format` (an export format, say)
moves the selector with `[assembly: OutputSelector("--output-format")]`. It
also states which category a handler returns: `Validation` (exit 2) for input
of the wrong shape, `CommandFailure` or `Unavailable` (exit 1) for a domain
failure on well-formed input; a handler that returned `Validation` for a
refused operation returns `CommandFailure`
([0.11.0 notes](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/eng/release/notes/0.11.0.md)).

Notification activations need a check since 0.11.0 (SEC-09): see
[Linux portal and D-Bus](#linux-portal-and-d-bus).

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

Both arguments are strings, so a call converted without swapping them compiles.
Name them as above. Since 0.14, `RUNICPLAT001` warns about a title that looks like
a command id (`"file.new"`) next to a command id that looks like a title or is not
a valid one, and its code fix swaps and names the arguments.

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
  only `http`, `https` and `mailto` links and are untrusted (SEC-09, 0.11.0):
  another process on the session bus can send one, and one also arrives after a
  restart. In `IDesktopNotifications.Activated`, check
  `DesktopNotificationActivation.IsTrusted` before acting on `NotificationId`,
  `ActionId` or `ActivationUri`. When it is `false`, look the identifiers up in
  the application's own state and act only on what you find there; don't open a
  document, run an action or follow a link because an activation names it.
  Windows and macOS activations, and Linux activations of a notification the
  running process showed, are trusted.
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
| Every published file in `Contents/MacOS` | Only Mach-O files; every other file moves to `Contents/Resources` with a relative symbolic link left in `Contents/MacOS`, and a folder without Mach-O files moves whole, leaving one link to the folder (a folder with Mach-O files stays and keeps only them; `www` moves to `Contents/Resources/www` without a link). `AppContext.BaseDirectory` lookups work unchanged; code that enumerates `Contents/MacOS` sees links. A script that copies into, signs or walks `Contents/MacOS` after `dotnet runic package` handles the links: it puts new non-code files in `Contents/Resources` (writing through a linked folder lands there anyway), copies the bundle with a tool that keeps links, such as `ditto`, signs the Mach-O files and not the links (the bundle's signature seals a link as a resource), and follows or skips links when it walks the folder |
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
| A custom `__runicBridge` or a test double with `call(name, …args)` and `onReconnect`, or code that wraps the host's `__runicBridge`, such as a wrapper that intercepts or logs its `call`s | `request(message): Promise<string>`, `onMessage(listener)` and the now required `onConnectionChange(listener)` ([Host bridge](../specs/application/README.md#host-bridge-page)). A wrapper wraps `request`, `onMessage` and `onConnectionChange` instead of `call`, and passes each through: a 0.13 client never calls `call`, so a wrapper of `call` sees nothing |
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

An application that opens its Windows through `RunicDesktopHost` turns the
checker off for every Window through the host options, starting from the
host's defaults:

```csharp
return RunicDesktopHost.Run(services, new DesktopHostOptions(), new RunicDesktopHostOptions
{
    WindowOptions = RunicDesktopHost.DefaultWindowOptions with { SpellChecking = false },
}, async host =>
{
    await using var window = await host.OpenWindowAsync<MainWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
```

An application that brings its own spell checker, such as an editor that draws
its own marks, turns the native checker off in those windows, so that the two
do not both mark words. Off means `spellcheck="false"` on each document's
`<html>`, which the fields inherit; an element with its own
`spellcheck="true"` is still checked by the native checker, so leave the
attribute off the editor. See
[Spell checking](../packages/dotnet/Runic.Desktop/README.md#spell-checking).

Details: 0.13.0 [Breaking changes](../eng/release/notes/0.13.0.md#breaking-changes),
[Behaviour changes](../eng/release/notes/0.13.0.md#behaviour-changes) and
[Added](../eng/release/notes/0.13.0.md#added).

## From 0.13 to 0.14

### Validation and DynamicData

| 0.13 | 0.14 |
| --- | --- |
| An empty validation message crossed as `""`. | It crosses as `Invalid value.`, in `validation` and in `…Errors`. Give the rule a message if the page shows another text. |
| A hand-written `ViewModelBridge<T>` subclass called the constructor ending in `collections`. | The constructor has a last optional `validationReader` parameter; existing calls compile. |

### Text splices and the handshake

0.14.0 adds the Views protocol feature `text-splice/1` and lets a page send its first requests with `hello`. Views
protocol 4 does not change: features are negotiated in `hello`, so a 0.13 page and a 0.14 host still connect and use
whole values. Update every Runic NuGet and npm package to 0.14.0 together and rebuild so that the generated clients,
bridges and mocks are regenerated.

| 0.13 | 0.14 |
| --- | --- |
| A setter or frame of a long `string` member carried its whole value, about 100 KB each way per keystroke at 100,000 characters | Splices of the changed characters ([Long text: splices](../packages/web/views/README.md#long-text-splices)). Nothing to change; `snapshot` still holds whole strings and the C# setter still receives the whole value |
| An operation started by 0.13 and recovered after the upgrade | `unknown`: every contract fingerprint changes (`runic-bridge-contract-v6`) |
| A request before `hello` (`InMemoryBridgeTransport.Request`, the mock Bridge, a custom producer test) answered `no-hello` at once | It waits up to 5 seconds for a `hello` and is admitted after it. Send the `hello` first, or advance a fake clock (`WindowContentSessionOptions.TimeProvider`, the mock's `scheduling: "manual"`) to get `no-hello`. A `receive` still answers at once |
| A test double for `__runicBridge` saw `open` only after it answered `hello` | `open` can arrive before the `hello` reply; answer them in the order a producer would. A double that answers a request sent with the `hello` with `no-hello` gets it again after the `hello` |
| Tests that asserted a refused producer received no `open` | The `open` sent with the `hello` reaches it; nothing is sent after the refusal |
| `RunicViewPublication` without splices | `Splices` (`RunicTextSplice`) on frames of connections that list `text-splice/1`; `InMemoryBridgeTransport`'s own `hello` lists no features, so driver tests keep `Sets` |
| Event 1019 `BridgeRequestBeforeHello` for every request before `hello` | Only for a request answered `no-hello` |

Details: 0.14.0 [Text splices and requests with `hello`](../eng/release/notes/0.14.0.md#text-splices-and-requests-with-hello-p1).

### Testing

| 0.13 | 0.14 |
| --- | --- |
| `driver.Snapshot()`, `Set`, `Write`, `Start`, `Mount`, `View`, `Track` and `transport.Request` in a suite that runs tests in parallel | `await driver.SnapshotAsync()`, `SetAsync`, `WriteAsync`, `StartAsync`, `MountAsync`, `ViewAsync`, `TrackAsync` and `RequestAsync`. The synchronous methods still work, but block a thread-pool thread per test while the window's asynchronous work needs the pool. |
| `ExecuteAsync(vm => vm.RenameCommand, """{"title":"Groceries"}""")` | `ExecuteAsync(vm => vm.RenameCommand, new RenameInput("Groceries"))`; for a string input name the type: `ExecuteAsync<string>(vm => vm.SearchCommand, "milk")`. |
| `SetJson(vm => vm.Filter, json)` for a DTO, list or union | `SetAsync(vm => vm.Filter, filter)`. |
| Parsing each `RunicBridgeMessage.Json` for its route | `message.Route` and `message.Kind`. |
| Registering a hand-built ViewModel so `RunicWindowTestHost.Create` resolves it | `RunicWindowTestHost.Create(services, viewModel)`. |
| `<RunicApplicationFrontendGenerationEnabled>false</…>` in a console test runner that references `Runic.Application.Testing` | Remove it, unless the project sets another property that requires generation. |

### Windows

| 0.13 | 0.14 |
| --- | --- |
| `((RunicDesktopHost)host).OpenWindowAsync<TWindow>(options, activation)`, or a separate `RunicDesktopHost` parameter, to open with a launch's activation context | `host.OpenWindowAsync<TWindow>(options, activation)` on any `IRunicWindowHost`. |
| A singleton that hands a value to the next Window's ViewModel, guarded by a semaphore | `windows.OpenWindowAsync<TWindow, TParameter>(parameter)`, and `IRunicWindowParameter<TParameter>` in the ViewModel's constructor. |

### Removed types

Since 0.14, a type removed in 0.9 or 0.13 that the code still names fails with
`CS0619` and its replacement instead of `CS0246`, for one release:

| Removed | Use |
| --- | --- |
| `[assembly: RunicBridgeJsonIgnore]` (0.9) | Delete it; add `[assembly: RunicIncludeJsonIgnored]` to keep `[JsonIgnore]` members |
| `RunicReactiveSchedulerProvider`, `IRunicReactiveSchedulerProvider` (0.9) | `ReactiveSchedulers.For(context)`, `ReactiveSchedulers.InstallMainThreadScheduler(loggerFactory)`, `IReactiveSchedulerProvider` |
| `INativePickerOwner` (0.9) | `INativeWindowOwner` |
| `IAsyncBridgeTransport`, `IBridgeUtf8Transport` (0.13) | `IBridgeTransport.Serve` and `BroadcastAsync`, `IBridgeConnectionTransport.SendAsync` |
| `IBridgeArguments` (0.13) | Rebuild: generated descriptors read the request's JSON value |
| `BridgeTestArguments` (0.13) | The `RunicViewDriver` methods, such as `SetAsync` and `ExecuteAsync(command, input)` |

A page that still calls the host's `__runicBridge.call(name)`, the protocol-3 API, now
gets one console warning that points to `__runicBridge.request`.

### Nix and the toolchain

| 0.13 | 0.14 |
| --- | --- |
| A package installed its own `share/mime/packages/<ApplicationId>.xml` (with `magic` or `sub-class-of`) in `postFixup`, after `buildRunicApplication` replaced it with a definition by extension | Install it in `postInstall`: the builder keeps it, and fails when it lacks a type of `RunicApplicationFileTypes` ([File types and URL schemes](../nix/README.md#file-types-and-url-schemes)) |
| One build per out-of-date `frontend.hash` or `dotnetTools.hashes` entry | `nix run .#<package>.fixedOutputHashes` prints them all ([Fixed-output hashes](../nix/README.md#fixed-output-hashes)) |
| .NET SDK 10.0.401 or Bun 1.4.2 built without a word | Warning `RUNICBRIDGE033`; `dotnet runic doctor` already failed. Install SDK 10.0.403 and Bun 1.4.4 or later, or add the code to `NoWarn` while nixpkgs still packages the older ones |

### Client notifications

| 0.13 | 0.14 |
| --- | --- |
| `subscribe` listeners ran once per frame, while the WebSocket message was handled, and `snapshot` changed with each frame | Once per animation frame with the newest state (a timer while the page is hidden); `snapshot` is the state listeners last received ([Notifications](../packages/web/views/README.md#notifications)). Code that counted notifications or logged every intermediate state sees only the newest. Several Views' changes can now reach the DOM in one task, so a `MutationObserver` callback can see records of elements that the same update removed. A setter's value and the result of an awaited call are still delivered at once |
| `snapshot` read right after a frame arrived, outside an awaited call | It can be up to one frame behind until the next animation frame; await the call whose result you need, or read the state in `subscribe` |
| A test double for `__runicBridge` saw subscribers notified during its `onMessage` call | Notified at the end of the task in an environment without `requestAnimationFrame` (Node, bun), or at the next animation frame where one exists (jsdom, happy-dom, browsers). The mock Bridge still delivers each push at once, and its `flush()` delivers the rest |
| A CS-WebUI page without its own `webui.setEventCallback` showed WebUI's "Connection with the backend is lost" banner | No banner: `runic-cswebui.js` installs an event callback, and WebUI shows the banner only without one. Show the connection state yourself, from `__runicBridge.onConnectionChange` or the Views connection-state stream ([Connection state](../packages/web/views/README.md#connection-state)) |

Details: 0.14.0 [Client and host performance](../eng/release/notes/0.14.0.md#client-and-host-performance-t2).

### Fields

0.14.0 adds a field API for every frontend framework and member type, over the generated `{View}Fields`
descriptors ([Fields](../packages/web/views/README.md#fields)). Text fields bound with `useTextField`,
`injectTextField` or `createTextFieldController` keep working; check the receipts row if their `write` is a checked
write.

| 0.13 | 0.14 |
| --- | --- |
| `useTextField(view, state => state.title, (client, value) => client.writeTitle(value, …))`: a `rejected` or `conflict` receipt resolved like a success and dropped the draft | The draft stays and `error` is a `FieldError` (`rejected`, `conflict`); `committed-with-error` sets `error`, and the receipt's `validation` is `errors`. Code that read the receipt itself to show an error can read `error` instead |
| Tests that compared the whole text field state, `{ value, editing, error }` | The state also has `text`, `errors` and `conflict` |
| A number or choice member bound through a command or a hand-made computed (`useCommand(value => client.setStep(value))`) | `useField(view, CounterFields.step)`: React `inputProps`, Vue `v-model="step.value"`, Svelte `{@attach step.attach}`, Angular `injectField` or `[runicField]` |
| App-local form adapters (Reactive Forms bridges, Svelte function bindings) | `[runicField]` from `@runic-artifex/angular/forms` with `[formControl]`, Signal Forms' `runicFieldModel` and `validateRunicField`; Svelte `useField` |
| Native `change` events or a hand-written debounce to write after a pause and on blur | `useField(view, field, { commit: "pause", delay })`; `await field.commit()` before a command that depends on the field, such as Save; it resolves with the field's state, so Save can skip a failed write |
| Showing `{name}Errors` or `validation.errors` next to an input by hand | The field's `errors` |

Details: 0.14.0 [Frontend field API](../eng/release/notes/0.14.0.md#frontend-field-api-f1).

### Dialogs, launcher and message boxes without a window

| 0.13 | 0.14 |
| --- | --- |
| Importing or exporting a file was impossible after the embedded window fell back to the installed browser (`OwnerUnavailable`) | `new OpenFileOptions(OwnerPolicy.PreferOwner)` (and `SaveFileOptions`, `OpenDirectoryOptions`) shows an unowned dialog there; owned windows are unchanged |
| A fallback window's `platform.files.open` and the other file capabilities were `OwnerUnavailable` | They describe the unowned dialogs of `PreferOwner` and can be `Available`; check `platform.dialogs.owned` for `RequireOwner` calls, or `platform.dialogs.unowned` |
| Code that treated `(OwnerPolicy)1` as undefined | It is `OwnerPolicy.PreferOwner` |
| `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`, `xdg-open` or a window's launcher borrowed from some window, to open a URI or reveal a file without a window | `IApplicationPlatformServices.FileLauncher` from the container, or `DesktopPlatformServices.ForApplication().FileLauncher`, with the launcher's URI policy and `PlatformResult` |
| A browser-fallback window's `IDesktopFileLauncher` reported `OwnerUnavailable` | It launches through the application's launcher until the window closes |
| `MessageBoxW` (or the platform's equivalent) P/Invoked for an error before any window exists | `ForApplication().MessageBoxes.ShowAsync(new DesktopMessageBox(text) { OwnerPolicy = OwnerPolicy.PreferOwner })`, or `IApplicationPlatformServices` from the container |
| A browser-fallback window's `platform.messageBoxes` was `OwnerUnavailable` | It describes the unowned boxes of `PreferOwner`; `RequireOwner` boxes still report `OwnerUnavailable` |

Details: 0.14.0 [Platform: dialogs, launcher and message boxes without a window](../eng/release/notes/0.14.0.md#platform-dialogs-launcher-and-message-boxes-without-a-window-a2).

### Navigation hooks under RunicModelContext

| 0.13 | 0.14 |
| --- | --- |
| With `RunicModelContext`, factories, guards, initialize, resume and arrival hooks and owned `Dispose` ran on the thread pool, outside turns | They run in the model context, each starting in a turn of its own, as with WPF's `DispatcherModelContext`. Set page state directly in the hook; `ModelContext.InvokeAsync` still works and runs inline. Don't block in a hook (`Wait()`, `Thread.Sleep`, synchronous I/O); await |
| A wrapper around `RunicModelContext` that implemented `IRunicModelHookScheduler` by posting the hook as a turn | Remove it; `RunicModelContext` does this itself |
| Content a factory returned for a rejected initial target was disposed before `CreateRegion` threw | It is disposed asynchronously; `WhenIdleAsync` waits for it |

Details: 0.14.0 [Navigation hooks on the model turn](../eng/release/notes/0.14.0.md#navigation-hooks-on-the-model-turn-a2).

### Connections

| 0.13 | 0.14 |
| --- | --- |
| A client connected by `useView`, `injectView` or `createViewController` was disposed, and its route closed, as soon as its component unmounted or its source changed | It stays connected for `keepAlive` milliseconds (2000 by default) and the next binding that follows the same connector takes it over ([Client lifetime](../packages/web/views/README.md#client-lifetime)). Tests that assert the route closes at unmount pass `{ keepAlive: 0 }` or wait for the period |
| `String(error)` in a status line ("BridgeError: …") | `describeBridgeError(error)`; `isBridgeError(error, "released")` instead of `error instanceof BridgeError && error.kind === "released"` |
| An app-local `bindTextInput` or `field-input.ts` for plain TypeScript pages | `bindField(element, client, EditorFields.title, options)` and `commitFields(...)` from `@runic-artifex/views` |
| SvelteKit: `runicDevSession()` in `vite.config.ts` | `runic({ html: false })`, and `import "@runic-artifex/vite-plugin-runic/client";` in `src/hooks.client.ts`, for the timeline and the stale-input warning |
| Angular: `runicDevSessionProxy()` only confirmed the dev session | It also receives the development client's reports; load it from `main.ts` with `if (typeof ngDevMode === "undefined" \|\| ngDevMode) await import("@runic-artifex/vite-plugin-runic/client");` |
| A tool that read `/__runic/state` from the Angular proxy expected 204 | 200 with the state JSON |

Details: 0.14.0 [Frontend connections and DX](../eng/release/notes/0.14.0.md#frontend-connections-and-dx-f2).

### CLI tools

The SDK tools are built with Runic Command Line 0.11.0. Exit code 2 now means
that the command line is wrong; a failure on what the command found exits 1.

| 0.13 | 0.14 |
| --- | --- |
| A missing or ambiguous project (`RAPPDEV1002`), a project MSBuild cannot evaluate (`RAPPDEV1003`), a missing program (`RAPPDEV1004`), an invalid Views Window project (`RAPPDEV1005`), a failed Window rebuild in `dev` (`RAPPDEV1006`), `--no-restore` without packages (`RAPPDEV1008`) or a missing Window entry (`RAPPDEV1009`) exited 2 | They exit 1. Test 1, or read `fault.code` from `--format json` |
| An existing `size --report` (`RAPPSIZE1003`), an invalid package identity (`RAPPPKG1001`) or missing Flathub sources (`RAPPPKG1007`) exited 2 | They exit 1; `package flathub` with neither or both of `--git-commit` and `--git-tag` still exits 2 |
| The packer's missing source directory (`RAS1001`) and a non-empty `runic-create` destination (`RCREATE003`) exited 2 | They exit 1 |
| Human standard error read `RAPPDEV1002: The selected project does not exist.` | It reads `The selected project does not exist.`, then the `Help for RAPPDEV1002` line; a usage error adds the usage line and the help command. Compare the message, or `fault.code` in JSON output |

Details: 0.14.0 [Runic Command Line 0.11.0 in the SDK tools](../eng/release/notes/0.14.0.md#runic-command-line-0110-in-the-sdk-tools-l1).

## From 0.14 to 0.15

### Desktop server

<!-- X2: replace this line with the batch's migration entries; remove it if the batch has none. -->

### Views protocol

0.15.0 adds the Views protocol features `presentation-epoch/1` and `presented/1`. Views protocol 4 does not change: a
0.14 page and a 0.15 host still connect and behave as in 0.14. Update every Runic NuGet and npm package to 0.15.0
together and rebuild so that the generated clients are regenerated.

| 0.14 | 0.15 |
| --- | --- |
| A presentation number in state, stamped on the root and every page and compared in each command, so that a late Next or Back does nothing | `[RunicPresentationScoped]` on the command and `RunicPresentation.Renew(this)` where the page changes; a call from an earlier page resolves without running ([Commands scoped to their page](../packages/dotnet/Runic.Application.Views/README.md#commands-scoped-to-their-page)) |
| A `UiRendered(id)` command called from the page after the next animation frame | `await RunicPresentation.WaitForPresentedAsync(viewModel, timeout)` |
| A close token and a close-acknowledged command with a timeout of its own | `CloseAsync(timeout)` waits for the page's acknowledgement within its timeout (at most 5 seconds); read `RunicWindowCloseResult.PagesAcknowledged` |
| A `switch` over `BridgeConnectionState` with `connecting`, `connected`, `reconnecting` and `lost` | Handle `"closed"` too: the application closed the window. It stays `closed` when the connection then drops |
| A kept client of content presented again showed its old state until `refreshPresentation` read it | The Bridge pushes the content's state when its presentations resume |
| A test that expected no push after `Main = b; Main = a` | A `state` push of the resumed content route |

Details: 0.15.0 [Views protocol (V5)](../eng/release/notes/0.15.0.md#views-protocol-v5).

### Navigation

<!-- N1: replace this line with the batch's migration entries; remove it if the batch has none. -->

### Embedded frontends

<!-- H1-758: replace this line with the batch's migration entries; remove it if the batch has none. -->

### Startup

<!-- S1: replace this line with the batch's migration entries; remove it if the batch has none. -->

### Native message path

<!-- X3: replace this line with the batch's migration entries; remove it if the batch has none. -->

### Cleanups

<!-- C1: replace this line with the batch's migration entries; remove it if the batch has none. -->

### Windows administration

| 0.14 | 0.15 |
| --- | --- |
| A missing GPMC (`0x80040154`) or WMI provider namespace or class (`0x8004100E`, `0x80041010`) was `AdministrationErrorCategory.Unavailable` | `AdministrationErrorCategory.NotInstalled`; `Unavailable` means an installed machine, endpoint or service cannot be reached. Code that matched `Unavailable` (or the raw HRESULT) to report a missing component matches `NotInstalled` |
| Native 1332, 1376, 1387, 2220 and 2221 (an account, local group or user that does not exist) were `NativeFailure`; 1388 was `NativeFailure` | `NotFound`; 1388 (an account that cannot join a local group) is `InvalidData` |
| Native 1460 (`ERROR_TIMEOUT`) was `NativeFailure` | `Unavailable` |
| Native 1051, 1061 and 1062 (a service with running dependents, a control the service cannot accept now, a service that is not running) were `NativeFailure` | `Conflict` |

Details: 0.15.0 [Windows administration](../eng/release/notes/0.15.0.md#windows-administration-w1).

### Desktop presentations

| 0.14 | 0.15 |
| --- | --- |
| A chain of presentations opened window after window, collecting the failures. | `DesktopWindowOptions.Fallbacks = [BrowserKind.ChromiumBased, BrowserKind.Firefox]`; catch `DesktopPresentationException` and read `Attempts`. |
| A failed `EmbeddedThenBrowser` fallback threw the browser's `DesktopException`, with the browser's underlying failure as inner exception. | It throws a `DesktopPresentationException` with the same `Code` and `Category`; `InnerException` is the browser's `DesktopException`. `catch (DesktopException)` still matches; a check for the exact type does not. |

Details: 0.15.0 [Runic Desktop: an ordered list of fallback presentations](../eng/release/notes/0.15.0.md#runic-desktop-an-ordered-list-of-fallback-presentations-h1).
