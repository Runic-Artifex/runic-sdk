# Experimental APIs

Some Runic APIs are marked `[Experimental]` while their shape can still change.
Each family has one diagnostic ID. The C# compiler reports every use of a marked
type or member as an error with that ID, and the message links to the entry on
this page for the release you use. Suppressing the ID is the opt-in: it says
your project accepts changes to that API before it is supported.

| ID | API family | Packages |
|---|---|---|
| [`RUNICWPF001`](#RUNICWPF001) | WPF child web presentation | `Runic.Application.Views.Wpf` |

## Suppressing a diagnostic

Suppress the ID for a whole project, for example in its `.csproj` or a
`Directory.Build.props` that also covers its test project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);RUNICWPF001</NoWarn>
</PropertyGroup>
```

Or suppress it where you use the API, for a file or a region of one:

```csharp
#pragma warning disable RUNICWPF001 // The WPF web View host is experimental.
RunicViewHost.SetServices(mainWindow, services);
#pragma warning restore RUNICWPF001
```

Code that the WPF markup compiler generates for you needs no suppression.
`Runic.Application.Views.Wpf` suppresses its ID in the project's intermediate
`.g.cs` files, where WPF declares a named control such as
`<rv:RunicViewHost x:Name="Editor" />`. The diagnostic is reported only in your
own code, so a per-site suppression is enough with
`TreatWarningsAsErrors`.

Navigation (`Runic.Navigation`, `Runic.Navigation.Wpf`, `Runic.Navigation.ReactiveUI`
and `Runic.Navigation.ReactiveUI.Reactive`) is no longer experimental as of
0.7.0-preview.7. Its former diagnostic ID is retired; remove its suppressions.

<a id="RUNICWPF001"></a>

## RUNICWPF001

**Covers:** the WPF child presentation of a Runic web View in
`Runic.Application.Views.Wpf`: `RunicWebView`, `WpfBridgeView<TViewModel>` and
`WpfBridgeViewExtensions` (`CreateWpfViewAsync`).

**Status:** experimental. It hosts Runic Desktop's WebView2 controller inside an
existing WPF visual tree, and its lifecycle API is still being shaped by the
[hybrid WPF example](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-hybrid-editor/README.md).
No stabilization criteria are published yet; removing `RUNICWPF001` is a
maintainer decision.
