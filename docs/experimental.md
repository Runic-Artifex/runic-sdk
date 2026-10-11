# Experimental APIs

Some Runic APIs are marked `[Experimental]` while their shape can still change.
Each family has one diagnostic ID. The C# compiler reports every use of a marked
type or member as an error with that ID, and the message links to the entry on
this page for the release you use. Suppressing the ID is the opt-in: it says
your project accepts changes to that API before it is supported.

| ID | API family | Packages |
|---|---|---|
| [`RUNICNAV001`](#RUNICNAV001) | Standalone navigation | `Runic.Navigation`, `Runic.Navigation.Wpf`, `Runic.Navigation.ReactiveUI`, `Runic.Navigation.ReactiveUI.Reactive`, parts of `Runic.Application` and `Runic.Application.Testing` |
| [`RUNICWPF001`](#RUNICWPF001) | WPF child web presentation | `Runic.Application.Wpf` |

## Suppressing a diagnostic

Suppress the ID for a whole project, for example in its `.csproj` or a
`Directory.Build.props` that also covers its test project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);RUNICNAV001</NoWarn>
</PropertyGroup>
```

Or suppress it where you use the API, for a file or a region of one:

```csharp
#pragma warning disable RUNICNAV001 // Runic navigation is experimental.
await regions.Main.PushAsync<NotesViewModel>();
#pragma warning restore RUNICNAV001
```

Code that Runic or the WPF markup compiler generates for you needs no
suppression. The generated Bridge suppresses `RUNICNAV001` for its navigation
region slots, and `Runic.Navigation.Wpf` and `Runic.Application.Wpf` suppress
their IDs in the project's intermediate `.g.cs` files, where WPF declares a
named control such as `<rn:NavigationHost x:Name="Host" />`. The diagnostic is
reported only in your own code, so a per-site suppression is enough with
`TreatWarningsAsErrors`.

<a id="RUNICNAV001"></a>

## RUNICNAV001

**Covers:** the standalone navigation API.

- `Runic.Navigation`: `RunicNavigator` and its options, `NavigationRegion<TContent>`
  and every other navigation type (entries, targets, results, departures,
  hooks such as `INavigationInitialize` and `INavigationDepartureGuard`, and
  `LeaveConfirmation`), `AddRunicNavigation()`, `IRunicModelContextLifetime`,
  `IRunicModelHookScheduler` and `RunicModelContext.Closed`. The model context
  types themselves (`IRunicModelContext`, `RunicModelContext`) are not
  experimental.
- `Runic.Navigation.Wpf`: every type, including `NavigationHost`,
  `NavigationDialogHost`, `NavigationSelector`, `ViewHost`,
  `DispatcherModelContext` and `AddRunicWpfNavigation()`.
- `Runic.Navigation.ReactiveUI` and `Runic.Navigation.ReactiveUI.Reactive`:
  `ReactiveNavigation`, the region observables and `CreateBackCommand`.
- `Runic.Application`: the `PropertyDescriptor<T>` constructor that observes a
  region slot. Generated Bridges call it; applications do not.
- `Runic.Application.Testing`: `RunicNavigationTestExtensions`.

**Status:** experimental for now (decision D018.7). The engine, its ownership
and lifetime rules, and its adapters are reviewed and tested, but the API can
still change between previews.

### Stabilization criteria

The maintainer removes `RUNICNAV001` when the following evidence is in place
(review W240-010):

1. **One engine.** Navigation keeps one engine in `Runic.Navigation`; the
   CommunityToolkit.Mvvm, ReactiveUI and WPF integrations stay optional and
   idiomatic, built on that engine rather than beside it.
2. **API review against documented contracts.** The public API matches its
   documented ownership, lifetime and threading contracts, verified by the
   core and consumer tests: guarded typed selection, cancellation, stale
   expectations, borrowed and owned disposal, joined Back requests and native
   ReactiveUI command composition.
3. **Maintained comparison scenarios.** The
   [WPF navigation example](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-navigation/COMPARISON.md)
   keeps covering the same app as Prism, ReactiveUI and CrissCross (W240-007).
4. **Consumer feedback.** Real CommunityToolkit.Mvvm and ReactiveUI
   applications, including the hybrid WPF migration, have used the API and
   their feedback is resolved. Open questions include whether a detached
   `NavigationHost` should release its old view before it is remounted.
   Nonblocking adoption limitations are recorded as follow-ups instead of
   holding back stabilization.

The criteria add no release gate. The maintainer decides whether the warning is
removed in a preview or at 1.0.

<a id="RUNICWPF001"></a>

## RUNICWPF001

**Covers:** the WPF child presentation of a Runic web View in
`Runic.Application.Wpf`: `RunicWebView`, `WpfBridgeView<TViewModel>` and
`WpfBridgeViewExtensions` (`CreateWpfViewAsync`).

**Status:** experimental. It hosts Runic Desktop's WebView2 controller inside an
existing WPF visual tree, and its lifecycle API is still being shaped by the
[hybrid WPF example](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-hybrid-editor/README.md).
No separate stabilization criteria are published yet; removing `RUNICWPF001` is
a separate maintainer decision from `RUNICNAV001`.
