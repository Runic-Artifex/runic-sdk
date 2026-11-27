# Packaging a Runic application

`dotnet runic package <target>` publishes a Runic application with NativeAOT
and packages it for one platform. This guide lists what each target needs on
the machine that builds it and on the machines that install it, and how you
sign the result with your own credentials. The command's options, manifests,
JSON output and `RAPPPKG` codes are in the
[dotnet-runic README](../tools/dotnet-runic/README.md#package-the-application).

Runic signs nothing for you: the packages are unsigned, or ad-hoc signed on
macOS, until you pass your certificate, Developer ID or key. That is enough to
install and test them on your own machines. To ship them, sign them as described
below.

| Target | Builds on | Writes | Install it with |
| --- | --- | --- | --- |
| `windows` | Windows | `<id>_<version>_<arch>.msix` | `Add-AppxPackage`, once signed |
| `macos` | macOS | `<name>.app`, `<name>-<version>.dmg`; with `--pkg` also `<name>-<version>.pkg` | Drag the bundle to Applications, or open the `.pkg` |
| `flatpak` | Linux | `<id>.flatpak` and a repository | `flatpak install --user <id>.flatpak` |
| `appimage` | Linux | `<Name>-<version>-<arch>.AppImage` | `chmod +x` and run it |
| `deb` | Linux | `<package>_<version>_<arch>.deb` | `sudo apt install ./<file>.deb` |
| `rpm` | Linux, also Debian and Ubuntu | `<package>-<version>-1.<arch>.rpm` | `sudo dnf install ./<file>.rpm` |
| `flathub` | Any OS; `--build` on Linux | A manifest directory for a Flathub submission | From Flathub, once accepted |

Each target refuses to run on another operating system, and takes one runtime
identifier of its operating system that Runic supports (`RAPPPKG1004`); a
missing tool stops it before it builds anything (`RAPPPKG1002`, which names the
package that provides the tool). Run every target in a CI job of its operating
system to ship all of them.

That job needs no Node.js or Bun when a Linux job builds the frontends: write
a web bundle there with `dotnet runic bundle`, upload it as an artifact, and
package from it with `--frontend-bundle` (since 0.24). The publish checks that
the bundle was built from the same ViewModels. `-p:NAME=VALUE` passes MSBuild
properties, such as `Version`, to every target but `flathub`. See
[Write a web bundle](../tools/dotnet-runic/README.md#write-a-web-bundle), and the
[split CI example](../examples/split-ci/README.md) for workflows that test and bundle on Linux and package
for Windows and Linux from the bundle.

### Building a Windows application on Linux or macOS

A project of a Windows target framework, such as a WPF application or model
project on `net10.0-windows`, restores and builds on Linux and macOS only with
`EnableWindowsTargeting=true`. Without it the restore stops with `NETSDK1100`.
The property must be set before the restore imports any package's build files,
so a Runic package can't set it. Put it in the application's own
`Directory.Build.props`, as the
[WPF hybrid editor](../examples/wpf-hybrid-editor/Directory.Build.props) does:

```xml
<PropertyGroup>
  <EnableWindowsTargeting Condition="'$(EnableWindowsTargeting)' == '' and !$([MSBuild]::IsOSPlatform('Windows'))">true</EnableWindowsTargeting>
</PropertyGroup>
```

`dotnet runic doctor` on Linux or macOS reports a Windows target framework
without it as its
[`windows-targeting`](../tools/dotnet-runic/README.md#windows-targeting) check.
With the property, a Linux CI job builds and tests such a project, writes its
[web bundle](../tools/dotnet-runic/README.md#write-a-web-bundle), and can
publish it framework-dependent for `win-x64`. The application still runs only
on Windows, so WPF and WebView2 tests, a NativeAOT `win-x64` publish, MSIX
packaging and signing need a Windows runner.

## Publishing with dotnet publish

Without the packaging command, `dotnet publish` produces a framework-dependent,
self-contained, self-contained trimmed (`-p:PublishTrimmed=true`) or NativeAOT
(`-p:PublishAot=true`) build of a Runic Desktop or CS-WebUI application. CI
publishes the Runic Desktop example self-contained and trimmed for `linux-x64`
and drives it in a browser. WPF can't be trimmed, so neither can an application
that uses `Runic.Application.Views.Wpf` or `Runic.Navigation.Wpf`: the .NET SDK
stops a trimmed WPF publish with `NETSDK1168`. Publish those framework-dependent,
or self-contained without `PublishTrimmed`.

### Publish for a runtime identifier

Publish a Runic application for the runtime identifier of the machines that
run it:

```sh
dotnet publish -c Release -r linux-x64 --self-contained false
```

Since 0.22 such a publish, framework-dependent or self-contained without
trimming, also with ReadyToRun or single-file, ships only its own operating
system's `Runic.Platform` assemblies, and the Linux-only libraries (the GirCore
GTK 4 bindings and `Tmds.DBus.Protocol`) only for Linux. `Runic.Desktop.Gtk4`
ships for every OS, since an application calls its `WithGtk4()` on each one.
Code that calls another operating system's `Runic.Platform` API directly, from
a method that also runs on this one, then gets `FileNotFoundException` instead
of the platform guard. `RunicPublishAllPlatformAssemblies=true` keeps every
operating system's assemblies
([MSBuild](msbuild.md#runicplatform-and-the-privileged-action-packages)).

Without `-r`, `dotnet publish` writes a portable framework-dependent folder.
It runs on every operating system with the .NET runtime, so it carries every
operating system's `Runic.Platform` assemblies and the native libraries of
every runtime identifier under `runtimes/`: the Runic Desktop native server for
seven, the WebView2 loader for three, and CS-WebUI's native library for each
runtime identifier it supports. Measured for the templates, that adds 8.5 MB to
a Runic Desktop application (6.1 MB of native libraries and 2.4 MB of other
operating systems' assemblies) and 1.6 MB to a CS-WebUI application, compared
with a `linux-x64` publish. A portable folder is a valid choice when one folder
must serve every OS; otherwise publish once per runtime identifier.
`dotnet runic package` always publishes for one runtime identifier (the host's
architecture unless you pass `--runtime`), and `dotnet runic size` requires
one.

### Size and cost of each publish mode

Each mode trades size for something else. The sizes below were measured on
2026-11-18 on runic-sdk `main` at 2a2aee3f, the 0.22 development line (.NET SDK
10.0.403, Release, `linux-x64` unless noted; `win-x64` and `osx-arm64`
cross-published from Linux), for applications created with
`dotnet new runic-app` from packages: the Runic Desktop template with its
defaults (React, ReactiveUI, native server) and the CS-WebUI template
(`--host cswebui`). They count every published file except PDBs, in MB of
1,000,000 bytes. They change with every release, with .NET and with your own
code and frontend, so measure your own application
([below](#measuring-your-own-application)).

| Mode | Desktop | CS-WebUI | Cost |
| --- | ---: | ---: | --- |
| Framework-dependent, `-r <rid>` | 10.7 | 6.1 | The machine needs the .NET runtime. `win-x64`: 11.1 and 6.5; `osx-arm64`: 9.4 and 6.1. |
| Framework-dependent, portable (no `-r`) | 19.2 | 7.7 | Every operating system's assemblies and every runtime identifier's native libraries ([above](#publish-for-a-runtime-identifier)). |
| ReadyToRun (`-p:PublishReadyToRun=true`) | 22.1 | 12.5 | Doubles the folder: each assembly carries native code next to its IL. Faster startup ([below](#startup-readytorun-and-invariant-globalization)). |
| Single-file (`-p:PublishSingleFile=true`) | 10.7 | 6.1 | One executable the size of the folder. The .NET SDK compresses a bundle only when it is self-contained, so framework-dependent single-file saves nothing. |
| Self-contained, trimmed | 30.8 | 27.5 | No .NET runtime needed on the machine; about 25 MB of it is the trimmed .NET runtime. |
| NativeAOT (`-p:PublishAot=true`) | 12.7 | 8.4 | No .NET runtime needed, fastest startup. The executable is 12.4 MB (Desktop) or 7.7 MB (CS-WebUI); the rest is the page and, for CS-WebUI, its native library. A build per OS on that OS. |

Template choices change the framework-dependent `linux-x64` size of the
Desktop template as well:

| Choice | Desktop | Cost |
| --- | ---: | --- |
| `--desktop-server kestrel` | 9.2 | Smaller folder: no native server, and ASP.NET Core and `Microsoft.Extensions.*` come from the shared ASP.NET Core runtime, which the machine must have in addition to the .NET runtime. Its NativeAOT executable is 15.5 MB. |
| `--linux-gtk 4` | 17.7 | The GirCore GTK 4 bindings add 6.8 MB and `Runic.Desktop.Gtk4` 0.2 MB. Windows and macOS publishes leave the bindings out (`win-x64`: 11.3) but keep `Runic.Desktop.Gtk4`, whose `WithGtk4()` the application calls on every OS. NativeAOT keeps about 1.5 MB of them: the executable is 13.9 MB. |
| `--view-models toolkit` | 8.6 | CommunityToolkit.Mvvm instead of the default ReactiveUI saves 2.1 MB on either host (CS-WebUI: 4.0 MB), almost all of it ReactiveUI's and Splat's assemblies. |

### Startup: ReadyToRun and invariant globalization

A framework-dependent or self-contained application spends most of its startup
compiling its own code and Runic's with the JIT. ReadyToRun compiles it ahead
of time, so we recommend it for every publish that is not NativeAOT:

```sh
dotnet publish -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=true
```

For the Runic Desktop example on the native server (linux-x64,
framework-dependent), it cuts the time until the first page is served from
about 170 to 115 ms, and makes the publish 15 to 25 MiB. ReadyToRun needs a
runtime identifier (`-r`). It is not a default; set
`<PublishReadyToRun>true</PublishReadyToRun>` in the application project to
keep it. NativeAOT already compiles everything ahead of time. Since 0.15 the
property no longer reaches the Views generator's own build, which failed with
`NETSDK1191`.

The host itself loads neither ICU nor, on Linux, OpenSSL before the first
window has its page (0.15). An application that formats or compares text for a
culture loads ICU when it first does, which costs about 2 ms and 5 MiB of
memory on Linux. The templates keep full globalization, so dates, numbers and
sorting follow the user's culture. An application whose interface shows no
culture-specific text can set `<InvariantGlobalization>true</InvariantGlobalization>`:
ICU is then never loaded, every culture formats and compares like the
invariant culture, and creating a culture other than the invariant one throws
`CultureNotFoundException` (unless `PredefinedCulturesOnly` is `false`).

### Making the executable smaller

Runic already keeps a NativeAOT or trimmed publish small without any setting
of yours:

- Its packages are built for trimming and NativeAOT, so the trimmer removes
  the Runic code an application does not reach, including the platform
  backends of other operating systems.
- Development-only code sits behind feature switches that are turned off in
  trimmed or NativeAOT publishes, such as `Runic.Application.DevelopmentServerForwarding`
  ([Security](security.md)), Runic Desktop's CS-WebUI remote-debugging
  endpoint (`Runic.Desktop.WebUiRemoteDebugging`) and `dotnet runic dev`'s live
  frontend directory (`Runic.Application.Views.Desktop.DevelopmentAssets`), so
  the trimmer removes it. Only the publish turns them off: builds and
  `dotnet run` keep them, also when the project sets `PublishAot`.
- Runic Desktop keeps only the presentation server that `RunicDesktopServer`
  selects, and none for `None`. An application that is never a hosted guest
  does not keep hosted mode, and one whose pages declare no hosted regions can
  leave the region host out with `RunicDesktopRegionHost=false`
  ([MSBuild](msbuild.md#runicapplicationviewsdesktop)).
- An application whose Bridges hold no `RunicBlob` and take no `RunicUpload`
  does not keep the Window's blob store, the upload folder or the hosts' blob
  endpoints: the generated codecs of a blob or upload member mark their
  assembly, and a trimmed or NativeAOT publish in which no assembly is marked
  or names `RunicBlob` or `RunicUpload` in its metadata turns `blobs/1` off (`Runic.Application.Views.Blobs`, 0.23). Its pages then
  get no upload grant, and `client.upload` rejects with `unavailable`.
  `RunicBridgeBlobs=true` keeps blobs in every publish ([MSBuild](msbuild.md)).
- On Linux and macOS, the CS-WebUI host serves blobs and uploads from a small
  loopback HTTP/1.1 server of its own instead of .NET's `HttpListener`, so the
  executable has no HTTP client, TLS or X.509 code for it (0.22).
- A hosted Runic Desktop region reads its component manifest without
  `HttpClient` (0.22).
- Runic Translations embeds its CLDR data and, from 0.14, each catalog's locale
  data compressed. Its Hot Reload support is off in a trimmed or NativeAOT
  publish, so the reflection it uses is removed.

Beyond that, four .NET settings make the executable smaller in exchange for
something the application gives up. Runic sets none of them and the templates
keep the defaults; each is the application's choice. Set them in the
application project:

```xml
<PropertyGroup>
  <StackTraceSupport>false</StackTraceSupport>
  <InvariantGlobalization>true</InvariantGlobalization>
  <OptimizationPreference>Size</OptimizationPreference>
  <UseSystemResourceKeys>true</UseSystemResourceKeys>
</PropertyGroup>
```

The savings were measured one setting at a time on linux-x64 for the CS-WebUI
reference application in [`tests/scaling/aot-size`](../tests/scaling/aot-size).
They vary by application, platform and .NET version, so measure your own
(below).

| Property | Saving | What the application gives up |
| --- | ---: | --- |
| `StackTraceSupport=false` | 574 KB | Stack traces have no method names, only offsets into the executable: `Exception.ToString()`, `Exception.StackTrace`, logged exceptions and crash output no longer say where an error happened. |
| `InvariantGlobalization=true` | 222 KB | .NET's culture-aware APIs behave like the invariant culture: `ToString()` and parsing of numbers and dates, `string.Compare`, sorting and casing with a culture, and `CultureInfo` for any other culture throws `CultureNotFoundException` (unless `PredefinedCulturesOnly` is `false`). Runic Translations' own formatting of numbers, dates, plurals and lists uses its CLDR data and is unaffected. See [Startup](#startup-readytorun-and-invariant-globalization). |
| `OptimizationPreference=Size` | 213 KB | The compiler optimizes for size rather than speed, so hot code can run slower. Measure the application's own hot paths. |
| `UseSystemResourceKeys=true` | none measurable in the reference application; larger applications may save more | .NET's own exception and error messages become resource keys, such as `Arg_NullReferenceException`, instead of sentences. The application's own messages and Runic's are unchanged. |

#### The smallest NativeAOT executable

Every choice and setting that makes a Runic NativeAOT executable smaller, with
what it saved where it was measured: linux-x64, Release, .NET SDK 10.0.403, on
the 0.22 development line, mostly in the size fixtures of
[`tests/scaling/aot-size`](../tests/scaling/aot-size) and their budgets
(`aot/` in [`budgets.json`](../tests/scaling/budgets.json)). Each figure is for
that application alone; they do not add up exactly, and your application's will
differ, so measure it ([below](#measuring-your-own-application)).

| Choice or setting | Default | Saving | Where it is described |
| --- | --- | ---: | --- |
| The native server (`Runic.Desktop.NativeServer`) instead of `Runic.Desktop.Kestrel` | the native server in new applications | 3.1 MB (Desktop template: 12.4 instead of 15.5 MB) | [Size and cost of each publish mode](#size-and-cost-of-each-publish-mode), [Runic.Desktop.NativeServer](../packages/dotnet/Runic.Desktop.NativeServer/README.md) |
| `RunicDesktopServer=None` for an executable that opens no Runic Desktop window | the referenced server | 46 KB without a server package; 4.6 MB when `Runic.Desktop.Kestrel` is referenced | [MSBuild](msbuild.md#runicdesktop) |
| GTK 3 instead of `Runic.Desktop.Gtk4` (`--linux-gtk 4`) | GTK 3 | 1.5 MB (13.9 instead of 12.4 MB) | [Size and cost of each publish mode](#size-and-cost-of-each-publish-mode) |
| `RunicDesktopRegionHost=false` for pages without hosted regions | `true` | 17 KB | [MSBuild](msbuild.md#runicapplicationviewsdesktop), [Hosted regions](hosted-regions.md) |
| An application that never starts as a hosted guest (`RunicGuestOptions.FromStandardInput()`) leaves the guest out | automatic | 509 KB | [Hosted applications](../packages/dotnet/Runic.Application.Views.Desktop/README.md#hosted-applications) |
| An application whose Bridges use no `RunicBlob` or `RunicUpload` leaves blobs and uploads out | automatic | 365 KB on CS-WebUI, 163 KB on Runic Desktop | [above](#making-the-executable-smaller), [Blobs and uploads](../packages/dotnet/Runic.Application.Views/README.md#blobs-and-uploads) |
| `dotnet runic dev`'s live frontend directory (`Runic.Application.Views.Desktop.DevelopmentAssets`) is off in a publish | automatic | 155 KB | [above](#making-the-executable-smaller) |
| Runic Desktop's CS-WebUI remote-debugging endpoint (`Runic.Desktop.WebUiRemoteDebugging`) is off in a publish | automatic | 50 KB | [above](#making-the-executable-smaller) |
| `[RunicDevelopmentOnly]` on a ViewModel that only development needs (`RunicDevelopmentOnlyViews`) | nothing marked | 15 KB per ViewModel | [Development-only views and members](#development-only-views-and-members) |
| An application that never creates a Runic Translations overlay leaves overlays out | automatic | 280 KB | [Ship translation updates without a release](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/ota.md) |
| `StackTraceSupport=false` | `true` | 574 KB | [above](#making-the-executable-smaller) |
| `InvariantGlobalization=true` | `false` | 222 KB | [above](#making-the-executable-smaller), [Startup](#startup-readytorun-and-invariant-globalization) |
| `OptimizationPreference=Size` | none (speed) | 213 KB | [above](#making-the-executable-smaller) |
| `UseSystemResourceKeys=true` | `false` | none measurable in the reference application | [above](#making-the-executable-smaller) |

The automatic rows need nothing from you as long as the application does not
use the feature; the others cost what their sections say. For the smallest
Runic Desktop executable, combine them:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <!-- Runic: only when the pages declare no hosted regions. -->
  <RunicDesktopRegionHost>false</RunicDesktopRegionHost>
  <!-- .NET: each gives something up, see the table above. -->
  <StackTraceSupport>false</StackTraceSupport>
  <InvariantGlobalization>true</InvariantGlobalization>
  <OptimizationPreference>Size</OptimizationPreference>
  <UseSystemResourceKeys>true</UseSystemResourceKeys>
</PropertyGroup>
```

with a reference to `Runic.Desktop.NativeServer` rather than `Runic.Desktop.Kestrel`, as the
template has.

#### Development-only views and members

A view that only development needs, such as a mock-control or diagnostics
panel, can stay out of the published executable. Mark its ViewModel, or single
properties and commands of another ViewModel, with `[RunicDevelopmentOnly]`
(0.22):

```csharp
[RunicDevelopmentOnly]
public sealed partial class MockControlsViewModel : ObservableObject { /* ... */ }

public sealed partial class OrdersViewModel : ObservableObject
{
    [ObservableProperty, RunicDevelopmentOnly] public partial string Diagnostics { get; set; } = "";

    [RelayCommand, RunicDevelopmentOnly] private void InjectFault() { /* ... */ }
}
```

Builds, `dotnet run`, the debugger and untrimmed publishes keep everything as
without the attribute. A trimmed or NativeAOT publish leaves the marked code
out, and the trimmer removes the generated Bridge of a marked ViewModel and
the code of marked members, with their codecs when nothing else uses them
(about 15 KB per ViewModel in the measurements of #1109):

- Opening a Window or View of a marked ViewModel, or presenting it as content,
  throws an `InvalidOperationException` that names it. Present it only when
  `RunicDevelopmentOnly.IsSupported`, which your own development-only code can
  test as well.
- A marked property is absent from the state, and the page cannot set or write
  it. A marked command, on its property or its `[RelayCommand]` method, and its
  `can…` and `is…Executing` state are absent too; calling or starting it is
  rejected with reason `unavailable`.

The generated client is the same in every configuration, so its types say what
a publish may do: a marked member is an optional state member, and the
documentation of a marked command notes that it may be unavailable. Marking a
member changes the View's contract; marking a ViewModel does not.

The MSBuild property `RunicDevelopmentOnlyViews` controls it: `auto`, the
default, leaves the marked code out of trimmed and NativeAOT publishes only;
`true` keeps it in every publish. The feature switch is
`Runic.Application.Views.DevelopmentOnlyViews`. Interactions and view requests
cannot be development-only on their own (`RUNICBRIDGE072`): mark their
ViewModel.

#### Measuring your own application

`dotnet runic size` publishes the application and writes a JSON report with
every published file and its size
([Measure size](../tools/dotnet-runic/README.md#measure-size)). It publishes
with `OptimizationPreference=Size` unless you pass
`--property OptimizationPreference=Speed`; pass the other settings the same way
to compare, for example `--property StackTraceSupport=false`.

To see what takes the space inside the executable, publish with
`-p:IlcGenerateMstatFile=true`. The NativeAOT compiler writes
`<application>.mstat` next to its intermediate output under `obj/`, which lists
the size of every type, method and data blob by assembly. Open it in
[sizoscope](https://github.com/MichalStrehovsky/sizoscope), which also compares
two builds.

## The manifests

The command fills manifests from the project file's identity: `ApplicationId`,
`ApplicationTitle`, `Version`, `Description`, `PackageProjectUrl`,
`RunicApplicationPublisher`, and the activation properties
`RunicApplicationUrlSchemes`, `RunicApplicationFileTypes`,
`RunicApplicationWebLinkHosts` and `RunicApplicationDBusActivatable`. It has a
built-in copy of each manifest. A file with the same path in the project's
`packaging/` directory (`RunicPackagingDirectory`) replaces the built-in one:
`windows/AppxManifest.xml`, `macos/Info.plist`, `macos/entitlements.plist`,
`linux/app.desktop`, `linux/app.metainfo.xml`, `linux/app.service` and
`linux/flatpak.yml`. A project created with `dotnet new runic-app --packaging`
has a copy of all of them to edit.

Before you publish a Linux package, edit `linux/app.metainfo.xml`: set the
project's license, a longer description and screenshots. Software centers and
Flathub show them.

`{{ApplicationName}}` and `{{Description}}` are escaped for the manifest they
are in: as XML in the XML manifests (`Notes &amp; Co`), and as desktop entry
strings in `linux/app.desktop` (`Notes & Co`). Before 0.17 the desktop entry got
the XML-escaped text too.

### Languages

Without more, the packages declare English only, so macOS shows its own text in
the application (the About panel, the open and save panels, the menu items it
adds) in English on a German Mac. List the languages the application is
translated into, the first being its development language:

```xml
<PropertyGroup>
  <RunicApplicationLocales>en;de;pt-BR</RunicApplicationLocales>
</PropertyGroup>
<ItemGroup>
  <!-- Optional: the application's name and description in a language. -->
  <RunicApplicationLocale Include="de" Name="Notizen" Description="Notizen für alle" />
</ItemGroup>
```

A tag is a BCP 47 language with an optional script, region and variants
(`de`, `pt-BR`, `zh-Hant`, `es-419`, `ca-ES-valencia`); `_` works as `-`, and
the case is made canonical (`pt_br` → `pt-BR`). An item may add a language the
property lacks. The packages then declare them:

| Package | What the languages add | Manifest field |
| --- | --- | --- |
| macOS | `CFBundleDevelopmentRegion` (the first language) and `CFBundleLocalizations`, and `Contents/Resources/<tag>.lproj` per language, with an `InfoPlist.strings` that names the application when the item has a `Name`. The development bundle of `dotnet run` has `CFBundleLocalizations` and the folders too. | `{{Localizations}}` in `macos/Info.plist` |
| MSIX | a `<Resource Language="…" />` row per language, the first being the default; `en-us` alone without the property. A translated display name needs a resource index (PRI), which the command does not make. | `{{Resources}}` in `windows/AppxManifest.xml`, inside `<Resources>` |
| Linux | `Name[xx]=` and `Comment[xx]=` in the desktop entry, `<name xml:lang>` and `<summary xml:lang>` in the metainfo, from the items' `Name` and `Description`, with the locale as `lang_COUNTRY@modifier` (see below). | `{{LocalizedEntries}}` in `linux/app.desktop`; `{{LocalizedNames}}` and `{{LocalizedSummaries}}` in `linux/app.metainfo.xml` |

macOS and MSIX use the tag itself. If any item has a `Name`, the bundle also
sets `LSHasLocalizedDisplayName`, so Finder and the Dock show the translated
name. Desktop entries and AppStream use glibc's locale names, so the Linux
packages map the tag:

| Tag | Linux locale | Rule |
| --- | --- | --- |
| `de`, `pt-BR` | `de`, `pt_BR` | A letter region is the country. |
| `es-419` | `es` | A numeric region is left out; glibc has no locale for it. |
| `sr-Latn`, `sr-Latn-RS`, `uz-Cyrl`, `ks-Deva` | `sr@latin`, `sr_RS@latin`, `uz@cyrillic`, `ks@devanagari` | Latn, Cyrl and Deva are the modifiers `@latin`, `@cyrillic` and `@devanagari`. |
| `zh-Hans`, `zh-Hant`, `zh-Hant-HK` | `zh_CN`, `zh_TW`, `zh_HK` | Chinese scripts name a region; a given region wins. |
| `pa-Arab` | `pa` | Another script is left out. |
| `ca-valencia`, `ca-ES-valencia` | `ca@valencia` | Without a script modifier, the first variant is the modifier of the language, as in gettext (`ca@valencia`). |

When two tags have the same Linux locale, the desktop entry and the metainfo
still name it once. Its name is the `Name` of the first of those tags that has
one, where the tags whose mapping keeps the whole tag (`zh-TW`, `es`) come
before the others (`zh-Hant`, `es-419`), each in the order of
`RunicApplicationLocales` and then the items. The description is chosen the same
way. So `zh-TW` wins over `zh-Hant` and `es` over `es-419`, and `zh-Hant` still
names `zh_TW` when `zh-TW` has no name.

A project created before 0.17 has manifests in `packaging/` without these
fields. Once it sets the languages, the command fails with `RAPPPKG1006`,
names the override file and the field, and says where to add it, rather than shipping a package that declares English
only.

An application that uses Runic Translations 0.15 or later needs no list: when
the project doesn't set `RunicApplicationLocales`, Runic.Translations.Build adds
the languages of its `runic.json` as `RunicApplicationLocale` items,
`baseLocale` first as the development language, then `locales`, without
`pseudoLocales`. A project that sets the property keeps exactly its value, its
own items still give a language its name and description, and several
`runic.json` files add their languages in item order. Set
`RunicTranslationsFillApplicationLocales` to `false` to turn this off
([Runic.Translations.Build](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.15.1/packages/dotnet/Runic.Translations.Build/README.md)).

## The application icon

An application supplies one icon: `RunicPackageIcon`, by default
`icons/icon.png`, a square PNG of 512 px (at least 256 px; 1024 px for the Mac
App Store), with an optional `icon.svg` beside it for Linux. The packages make
their icons from it, and since 0.23 the build derives the rest:

| Where | What Runic derives | When |
| --- | --- | --- |
| Windows executable | `obj/<configuration>/<framework>/runic-icon/application.ico` with 16, 20, 24, 32, 40, 48 and 64 px as 32-bit bitmaps and 256 px as PNG, used as `ApplicationIcon`: the icon in Explorer and on the taskbar, embedded in the managed assembly, the application host and the NativeAOT executable | Every build of an executable whose project sets no `ApplicationIcon` or `Win32Resource` |
| Windows windows | A Runic Desktop window without `IconFile` takes its large and small icon from the executable's icon resource, or from the application's assembly when the process is `dotnet app.dll`; nothing is shipped for it | At run time |
| macOS development bundle | `Contents/Resources/icon.icns` with `CFBundleIconFile`, PNG entries of the sizes `iconutil` writes, up to the icon's size, so the Dock shows the icon during `dotnet run` | `dotnet run` on a Mac ([development bundle](../packages/dotnet/Runic.Platform.MacOS/README.md#development-bundle)) |
| macOS outside a bundle | A Runic Desktop window without `IconFile` sets the PNG, copied beside the executable as `runic-application-icon.png`, as the application's icon when the process has no bundle icon, such as `dotnet app.dll` | Builds that can run on macOS (no `RuntimeIdentifier`, or `osx-*`) |

The `.ico` and `.icns` are written by managed code in `Runic.Platform`'s build
files, without native libraries or tools such as ImageMagick, `sips` or
`iconutil`, so a Linux build, a Windows build, a Mac and a Nix build write the
same bytes. Every standard PNG works: each colour type and bit depth, palette
transparency (`tRNS`) and interlaced images. The sizes are scaled down with an
area filter in premultiplied alpha, so transparent pixels add no colour to the
edges. An icon that is not square, smaller than 256 px or no readable PNG is a
build warning ([`RUNICICON001`](diagnostics.md#runicicon001) to
[`RUNICICON003`](diagnostics.md#runicicon003)), and the build goes on without
it.

GTK takes a window's icon from the icon theme by the application ID, as before:
the installed packages put the icon there. To use an `.ico` of your own, set
`ApplicationIcon`; `RunicApplicationIconGenerationEnabled=false` turns all of
the above off.

## Windows

Install the Windows SDK, which provides `makeappx` and `signtool`. The command
finds them on `PATH` or in the newest `Windows Kits\10\bin\<version>\<arch>`
folder.

Windows installs only signed MSIX packages. The package's `Publisher`
(`RunicApplicationPublisher`, or `--publisher`) must equal the subject of the
certificate that signs it:

- **A PFX file:** `--certificate app.pfx`, with its password in the
  `RUNIC_SIGNING_PASSWORD` environment variable. The command never prints the
  password.
- **The certificate store or a hardware token:** `--thumbprint <sha1>`.
- **A timestamp,** so the signature outlives the certificate:
  `--timestamp-url <url>` from your certificate's provider.
- **A cloud signing service** (Azure Trusted Signing, a signing HSM): package
  without signing options, then sign the unsigned `.msix` with the service's own
  `signtool` options.
- **The Microsoft Store** signs packages itself; upload the unsigned package.

To test a package on your own machine, create a self-signed certificate whose
subject is the publisher and trust it in `Cert:\LocalMachine\TrustedPeople`; the
payload then reports `"signing": "test"`.

## macOS

Install Xcode's command line tools (`xcode-select --install`), which provide
`codesign`, `iconutil` and `notarytool`. The command signs the bundle
with the hardened runtime and `macos/entitlements.plist`, and checks it with
`codesign --verify --deep --strict`.

The bundle keeps only code in `Contents/MacOS`, as notarization expects: the
executable and any other Mach-O file the project publishes, such as a native
library or a helper tool. The frontend goes to `Contents/Resources/www`, where
Runic looks for it, and every other published file or directory (configuration,
data) to `Contents/Resources`, with a relative symbolic link in its place in
`Contents/MacOS`, so code that reads it next to the executable
(`AppContext.BaseDirectory`) still finds it. Nested libraries and executables are
signed one by one with the hardened runtime, deepest first, before the bundle; in
the App Sandbox a nested executable gets `com.apple.security.inherit`.

Without options the bundle is ad-hoc signed: it runs on the Mac that built it,
and Gatekeeper blocks it on other Macs. To ship it you need an Apple Developer
Program membership:

1. Install a **Developer ID Application** certificate in the login keychain and
   pass its name: `--sign "Developer ID Application: Example Ltd (TEAMID)"`.
   The bundle and the disk image are signed with a secure timestamp.
2. Store notarization credentials once, with an app-specific password or an App
   Store Connect API key: `xcrun notarytool store-credentials notary`.
3. Pass the profile: `--notary-profile notary`. The command submits the disk
   image, waits for Apple's result and staples the ticket to it.

In CI, import the certificate into a temporary keychain first (for example with
`security create-keychain` and `security import`), and keep the certificate
and the notarization key in your CI system's secrets.
The bundle is for Apple silicon (`osx-arm64`). Runic does not target Intel
Macs: `--runtime osx-x64` and universal bundles (`osx-arm64,osx-x64`), which
0.11 made with `lipo`, are refused with `RAPPPKG1004`.

Universal links for `RunicApplicationWebLinkHosts` need the
`com.apple.developer.associated-domains` entitlement and a provisioning
profile, which an ad-hoc signature cannot carry. Add the entitlement to your
`macos/entitlements.plist` override and pass the profile with
`--provisioning-profile` (see below).

### File types

Each extension in `RunicApplicationFileTypes` becomes a document type in
`Info.plist`. A type the application does not own must not claim the `Owner`
rank, or macOS treats the application as the creator of every `.txt` or `.png`
file. The command therefore distinguishes:

- **System types.** `.txt`/`.text`, `.md`/`.markdown`, `.log`, `.json`, `.xml`,
  `.yaml`/`.yml`, `.csv`, `.tsv`, `.html`/`.htm`, `.rtf`, `.pdf`, `.js`, `.sh`,
  `.py`, `.png`, `.jpg`/`.jpeg`, `.gif`, `.heic`, `.tif`/`.tiff`, `.bmp`, `.ico`,
  `.webp`, `.svg`, `.mp3`, `.wav`, `.mp4`, `.mov`, `.zip`, `.vcf` and `.ics` name
  the system's uniform type identifier (`public.plain-text`, `public.png`, …)
  with the `Alternate` rank: Finder offers the application under *Open With*
  without making it the default. `net.daringfireball.markdown` is also imported
  (`UTImportedTypeDeclarations`), because not every macOS version declares it.
- **Your own types,** any other extension, export
  `<ApplicationId>.<extension>` (`UTExportedTypeDeclarations`, conforming to
  `public.data`) with the `Owner` rank.

Two properties adjust it:

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicMacFileTypeIdentifiers` | none | `.extension=identifier` pairs, separated by `;`, for types that another application or the system declares but the list above does not know, such as `.log=public.log` or `.sketch=com.bohemiancoding.sketch.drawing.single`. The bundle imports their declaration (unless it is one of the system types above) and uses the handler rank. |
| `RunicMacFileTypeHandlerRank` | `Alternate` | `Default` asks to be the default application for the types the application does not own. |

### Camera and microphone

A page that calls `getUserMedia` in a window with
`DesktopPermissionGrant.MediaCapture` needs the application's own camera or
microphone access. macOS grants it only to a bundle that explains why in
`Info.plist` and, under the hardened runtime or the App Sandbox, has the device
entitlement. Set the explanation, which macOS shows when it first asks the
user:

```xml
<RunicMacCameraUsageDescription>Notes shows your camera in video notes.</RunicMacCameraUsageDescription>
<RunicMacMicrophoneUsageDescription>Notes records voice notes.</RunicMacMicrophoneUsageDescription>
```

Each adds `NSCameraUsageDescription` or `NSMicrophoneUsageDescription`
(the `{{UsageDescriptions}}` field of `macos/Info.plist`) and
`com.apple.security.device.camera` or `com.apple.security.device.audio-input`
(the `{{Entitlements}}` field of `macos/entitlements.plist`). An override
without the field fails with `RAPPPKG1006` rather than shipping a bundle that
cannot use the device. Without the properties, WKWebView rejects
`getUserMedia` with `NotAllowedError` and macOS never asks the user, even with
the `MediaCapture` grant; an application whose own code opens the device
without a usage description is terminated by macOS.

### App Sandbox and the Mac App Store

Sandboxing is opt-in. `--sandbox`, or `<RunicMacSandbox>true</RunicMacSandbox>`
in the project, signs the bundle with these entitlements:

| Entitlement | Why Runic needs it |
| --- | --- |
| `com.apple.security.app-sandbox` | The App Sandbox itself |
| `com.apple.security.network.server` | Runic serves the page from a loopback listener and accepts the bridge's WebSocket |
| `com.apple.security.network.client` | WKWebView connects to that listener |
| `com.apple.security.files.user-selected.read-write` | File dialogs grant the files the user selects |
| `com.apple.security.files.bookmarks.app-scope` | `CreateFileBookmarks()` keeps access to them across launches |

The command fills them into `macos/entitlements.plist` where the template has
`{{Entitlements}}`. An override without that field must list the keys itself:
with `--sandbox`, a missing key is `RAPPPKG1006` rather than a bundle that
silently runs unsandboxed. Add other entitlements your application needs to the
override; the camera and microphone entitlements come
from their usage descriptions (see above). A window whose
`DesktopWebContentPolicy.AllowDownloads` is set saves downloads to the user's
Downloads folder, which a sandboxed application reaches only with
`com.apple.security.files.downloads.read-write`; Runic does not add it, because
downloads are refused by default, so add it to the override when you allow
them. Without it a download fails and nothing is saved.

In the sandbox the application reaches its container, the files the user
selected or dropped, and bookmarked files; privileged actions and volume
management are not available, and a document app cannot create sibling files
such as lock files ([Runic.Platform.MacOS: App Sandbox](../packages/dotnet/Runic.Platform.MacOS/README.md#app-sandbox)).
Sandbox an application when it needs none of those, or when you ship it in the
Mac App Store.

For the Mac App Store, add these to the sandboxed build:

1. `<RunicMacApplicationCategory>public.app-category.productivity</RunicMacApplicationCategory>`
   (one of Apple's `public.app-category.*` identifiers), which becomes
   `LSApplicationCategoryType`; App Store Connect rejects a bundle without it.
2. A 1024 px `RunicPackageIcon`: the icon set includes the 512 px icon at 2x,
   which the App Store requires, scaled up from a 512 px source.
3. An **Apple Distribution** certificate (or **3rd Party Mac Developer
   Application**) for `--sign`, and a Mac App Store provisioning profile for
   the `ApplicationId` from your developer account for
   `--provisioning-profile`. The command embeds it as
   `Contents/embedded.provisionprofile` and adds its
   `com.apple.application-identifier` and
   `com.apple.developer.team-identifier` to the entitlements; a profile for
   another application is `RAPPPKG1006`.
4. `--pkg` with `--installer-sign "3rd Party Mac Developer Installer: …"`:
   `productbuild` writes `<name>-<version>.pkg`, which installs the bundle in
   `/Applications`, signed with your installer certificate. Upload it with
   Apple's Transporter app; Runic uploads nothing.

```sh
dotnet runic package macos --sandbox \
  --sign "Apple Distribution: Example Ltd (TEAMID)" \
  --provisioning-profile Notes_Mac_App_Store.provisionprofile \
  --pkg --installer-sign "3rd Party Mac Developer Installer: Example Ltd (TEAMID)"
```

If your application uses only the encryption in the operating system, such as
HTTPS, you can add `ITSAppUsesNonExemptEncryption` with `<false/>` to your
`macos/Info.plist` override; otherwise App Store Connect asks about export
compliance for each build. That answer is yours to give, so the command does
not set it.

Outside the App Store, `--pkg` with a **Developer ID Installer** identity
writes an installer package that `--notary-profile` notarizes and staples like
the disk image. A `.pkg` without `--installer-sign` is unsigned
(`RAPPPKG2001`): it installs on the Mac that built it, for testing.

Runic's CI packages a sandboxed Runic Desktop application with `--pkg`,
ad-hoc signed and with an unsigned installer package, and runs it in the
sandbox. It has no Apple certificates or provisioning profiles, so real
signing, the profile's identifiers, App Store Connect validation and
TestFlight are checked only by your own distribution build.

## Flatpak

On Debian and Ubuntu install `flatpak flatpak-builder appstream
appstream-compose librsvg2-common desktop-file-utils`; on Fedora `flatpak
flatpak-builder appstream desktop-file-utils`. Add the Flathub remote, from
which flatpak-builder installs the GNOME runtime and SDK on the first build:

```sh
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
```

The GNOME runtime provides GTK 4 and WebKitGTK 6, so the project needs the GTK 4
backend (`Runic.Desktop.Gtk4`, `dotnet new runic-app --linux-gtk 4`). The sandbox
gets network access because Runic serves the page to the WebView over loopback.

`--gpg-sign <key id>` signs the repository and the bundle with your key. Serve
the repository and publish the key's public part so users can add it as a
remote.

## AppImage

Install `appimagetool` from the
[appimagetool releases](https://github.com/AppImage/appimagetool/releases) on
`PATH`, and put the matching AppImage runtime (`runtime-x86_64` or
`runtime-aarch64` from the
[type2-runtime releases](https://github.com/AppImage/type2-runtime/releases))
beside it, or name the runtime file with `RUNIC_APPIMAGE_RUNTIME`. The command
then downloads nothing while packaging. If `appstreamcli` is installed,
appimagetool validates the metainfo too. On a machine without FUSE 2, set
`APPIMAGE_EXTRACT_AND_RUN=1` so appimagetool, itself an AppImage, runs.

The AppImage contains the application and its frontend, but not GTK or
WebKitGTK: it uses the target system's, as a deb or rpm does. Bundling WebKitGTK
would add about 100 MB and break, because WebKitGTK's helper processes are
found at paths compiled into it. Document the requirement for your users:

- Debian and Ubuntu: `libwebkit2gtk-4.1-0` (GTK 3) or `libwebkitgtk-6.0-4` (GTK 4)
- Fedora: `webkit2gtk4.1` or `webkitgtk6.0`
- openSUSE: `libwebkit2gtk-4_1-0` or `libwebkitgtk-6_0-4`
- Arch Linux: `webkit2gtk-4.1` or `webkitgtk-6.0`

An AppImage registers no URL schemes or file types by itself; an integration
tool such as Gear Lever or AppImageLauncher installs its desktop entry. It is not
D-Bus activatable, because it has no fixed path. To sign it, run
`appimagetool --sign --sign-key <key id>` on the AppDir yourself, or sign the
file with GPG and publish the signature beside it.

## deb

Install `dpkg` (Debian and Ubuntu have it; `desktop-file-utils` adds the desktop
entry check). The package depends on the distribution's GTK and WebKitGTK:
`libgtk-3-0t64 | libgtk-3-0, libwebkit2gtk-4.1-0`, or `libgtk-4-1,
libwebkitgtk-6.0-4` for a project that references `Runic.Desktop.Gtk4`. Ubuntu
24.04 and Debian 13 have these packages.

A deb itself is not signed: apt checks the signature of the repository that
serves it. Publish it in a repository signed with your key, for example with
`reprepro` or `aptly`.

## rpm

Install `rpm-build` on Fedora, or `rpm` on Debian and Ubuntu, which provides
`rpmbuild`. The package requires `webkit2gtk4.1`, or `webkitgtk6.0` for GTK 4, and
nothing else: rpm's automatic dependencies are off because the NativeAOT
binary loads GTK and WebKitGTK with `dlopen`, so rpm would not find them. Its
`License` is `PackageLicenseExpression`.

Sign the package with `rpmsign --addsign <file>.rpm` and your GPG key (set
`%_gpg_name` in `~/.rpmmacros`), and publish the key so users can import it
with `rpm --import`.

## Flathub

Flathub builds every application from source in its own sandbox, without
network access, and signs its repository itself. `dotnet runic package flathub`
writes the manifest for that build; you submit it from your own account (Runic
submits nothing). The manifest directory holds:

- `<ApplicationId>.yml`: the GNOME runtime, the `org.freedesktop.Sdk.Extension.dotnet10`
  and, with a frontend, `org.freedesktop.Sdk.Extension.node24` SDK extensions, a
  `git` source at your commit or tag, and the build: `npm ci --offline` in the
  frontend, then `dotnet publish` with NativeAOT for the builder's architecture
  from the offline NuGet sources.
- `nuget-sources.json` and `npm-sources.json`: every package of your lock files
  as a file source with its URL and SHA-512.
- The desktop entry and metainfo, filled from the project as for the Flatpak
  target.

The project needs lock files that list everything the build restores:

1. Restore with lock files (`<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`)
   and give the NativeAOT restore its own lock file, for both Flathub
   architectures:

   ```xml
   <RuntimeIdentifiers>linux-x64;linux-arm64</RuntimeIdentifiers>
   <NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">packages.aot.lock.json</NuGetLockFilePath>
   ```

2. Run `dotnet restore -p:PublishAot=true` and commit `packages.aot.lock.json`.
3. Use npm for the frontend and commit its `package-lock.json`.

Then generate the manifest for the commit or tag you submit:

```sh
dotnet runic package flathub --git-url https://github.com/example/notes.git --git-tag v1.2.0
dotnet runic package flathub --git-url https://github.com/example/notes.git --git-tag v1.2.0 --build   # on Linux
```

The command restores the NativeAOT graph for both architectures in locked mode
and takes each package's SHA-512 from the NuGet cache, after checking it
against the lock file's content hash (nuget.org serves the package with its
repository signature, so the file's hash differs from the lock file's). It pins
the build's .NET runtime to the lock file's NativeAOT compiler, so the SDK
extension, which Flathub updates, restores the same packages. Generate the
manifest again whenever a lock file changes. `--build` installs the runtime and
extensions from Flathub, downloads every source, builds without network access
as Flathub's builders do, and writes `<ApplicationId>.flatpak` to install and
try. When the install fails on a download from Flathub, it runs once more after
30 seconds; any other failure, and a second download failure, ends the build.

To submit, follow Flathub's
[submission guide](https://docs.flathub.org/docs/for-app-authors/submission):
fork `flathub/flathub`, add the manifest directory on a branch from
`new-pr`, and open a pull request. Flathub's review asks for a complete
metainfo file (license, description, screenshots, release notes) and the
narrowest `finish-args`; the manifest's `--share=network` is needed because
Runic serves the page to the WebView over loopback, and its
`--talk-name=com.canonical.AppMenu.Registrar` lets the Linux global menu reach
KDE Plasma's menu registrar, so explain both in the submission.

For a project without lock files, or with another package manager, generate
the sources with [flatpak-builder-tools](https://github.com/flatpak/flatpak-builder-tools)
instead: `dotnet/flatpak-dotnet-generator.py` and `node/flatpak-node-generator`
restore the project to find its packages.

## Snap

Snap is not a `dotnet runic package` target. This recipe packs the deb's staged
tree into a strict snap on `core24` with the `gnome` extension, which provides
GTK 3, GTK 4 and WebKitGTK. Run `dotnet runic package deb` first, then extract
the package next to `snap/snapcraft.yaml`:

```sh
dpkg-deb -x artifacts/package/deb/<package>_<version>_amd64.deb snap/local/root
```

```yaml
# snap/snapcraft.yaml
name: example-notes            # the snap name, lower case
base: core24
version: '1.2.3'
summary: Notes for everyone
description: |
  Notes for everyone.
grade: stable
confinement: strict
platforms:
  amd64:

apps:
  example-notes:
    command: usr/lib/org.example.Notes/Notes
    extensions: [gnome]
    desktop: usr/share/applications/org.example.Notes.desktop
    common-id: org.example.Notes
    plugs:
      - desktop
      - wayland
      - x11
      # Runic serves the page to the WebView over loopback.
      - network
      - network-bind

parts:
  application:
    plugin: dump
    source: snap/local/root
```

Build it on Ubuntu with `snapcraft pack`, and install the result with
`sudo snap install --dangerous ./<name>_<version>_amd64.snap` to try it. The
desktop entry's `MimeType=` registers the URL schemes and file types once the
snap is installed; snapd rewrites its `Exec=` to start the snap. To publish, register the name and upload with
`snapcraft upload`; the Snap Store signs the snap.

CI does not build this recipe; the maintainers check it by hand on Ubuntu. Treat
it as a starting point for your own snap.


## Updates

Runic has no update API. How an installed application gets a new version
depends on how it was installed:

| Installed from | Updated by |
| --- | --- |
| MSIX | An `.appinstaller` file next to the package, the Microsoft Store, or your device management |
| Flatpak, Flathub | `flatpak update` and the software center |
| Snap | snapd, from the Snap Store |
| deb, rpm | The distribution's package manager, from your apt or dnf repository |
| Nix | The user's flake or channel update |
| The macOS bundle or a direct download | The application itself, for example with Velopack |

For a direct download, [Velopack](https://docs.velopack.io/) installs, updates
and restarts a .NET application on Windows, macOS and Linux. It packs a
`dotnet publish` output, which includes the frontend's `www/` web root, instead
of `dotnet runic package`, so ship one or the other for a given platform. Run
Velopack's hooks first in `Main`, before the Runic host starts:

```csharp
using Velopack;

VelopackApp.Build().Run();
return RunicDesktopHost.Run(provider, async host => { /* windows */ });
```

Check for an update from a ViewModel command or a background service, and let
the page ask before restarting; `ApplyUpdatesAndRestart` ends the process, so
close or save the windows' work first:

```csharp
var updates = new UpdateManager("https://example.com/releases");
if (await updates.CheckForUpdatesAsync() is { } update)
{
    await updates.DownloadUpdatesAsync(update);
    updates.ApplyUpdatesAndRestart(update);
}
```

Publish with `dotnet publish -c Release -r <rid>`, then pack the directory with
`vpk pack` (`--packId`, `--packVersion`, `--packDir`, `--mainExe`) and upload the
release files to the address the `UpdateManager` reads. Velopack signs and
notarizes on macOS and signs on Windows with your own credentials, as described
above for Runic's packages.
