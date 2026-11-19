# MSBuild properties and items

Every MSBuild property and item that a Runic SDK package or tool reads from an
application's project, by package. Each row links to the section that describes
it in full; the defaults there are the reference. The
[release policy](../eng/release/README.md#what-counts-as-breaking) covers the
names on this page: a removed or renamed one, or a changed default that breaks a
build, is a breaking change.

Not on this page, and not settings: names that start with `_Runic`, the
targets of the Runic packages, the Bridge generator's command line and the files
it writes under `obj/`. A target's name and order are internal, so run your own
targets around MSBuild's (`AfterTargets="Build"`), except for the three extension
points that Runic.Application.Views documents, `RunicViewsGenerateBridge`,
`RunicViewsBuildFrontend` and `RunicViewsCopyFrontend`
([Build properties](../packages/dotnet/Runic.Application.Views/README.md#build-properties)).
Internal properties and items start with `_Runic`. Two exceptions predate that rule:
Runic.Desktop still reads `RunicDesktopKestrelReferenced` and `RunicDesktopNativeServerReferenced`, the markers
that the server packages of 0.20 and earlier set (since 0.21 they set `_RunicDesktopKestrelReferenced` and
`_RunicDesktopNativeServerReferenced`); they are internal, not settings. The `RunicTranslationsGeneratedFile` item is
the handshake between Runic Translations' build, which adds the files it
generates, and Runic.Application.Views, which mirrors them into the frontend
(`RunicApplicationFrontendTranslations`); it is not a setting either, and the
two packages change it together. A renamed setting keeps its old name working with a warning; the former
names are listed in
[Moved and renamed since 0.7.0-preview.6](../packages/dotnet/Runic.Application.Views/README.md#moved-and-renamed-since-070-preview6)
and the [migration guide](migration-1.0.md).

## Runic.Application.Views

See [Build properties](../packages/dotnet/Runic.Application.Views/README.md#build-properties).
The host adapters (Desktop, CS-WebUI, WPF) bring these targets in.

| Name | Kind | Default | Purpose |
| --- | --- | --- | --- |
| `RunicApplicationFrontendDirectory` | property | `Frontend` | The frontend's `package.json` directory. |
| `RunicApplicationFrontendGeneratedDirectory` | property | `<frontend>/src/generated` | Where the generated TypeScript clients go. |
| `RunicApplicationFrontendPackageManager` | property | from `package.json` or the lock file, else `npm` | `npm`, `pnpm` or `bun`. |
| `RunicApplicationFrontendBuildCommand` | property | the package manager's `run build` | The production frontend build. An optimized build (`Optimize=true`: Release, `dotnet publish`) runs it with `NODE_ENV=production` unless the environment sets `NODE_ENV`, so Bun's bundler resolves the production build of `@runic-artifex/views`; a Debug build leaves `NODE_ENV` unset. |
| `RunicApplicationFrontendInstallCommand` | property | the package manager's locked install | The install before the build. |
| `RunicApplicationFrontendAllowUnlockedInstall` | property | `false` | Install without a lock file. |
| `RunicApplicationFrontendInstallEnabled` | property | `true` | `false` when a workspace install owns the packages. |
| `RunicApplicationFrontendNodeModulesDirectory` | property | none | A `node_modules` installed elsewhere, such as by the Nix builder. |
| `RunicApplicationFrontendBuildEnabled` | property | `true` | `false` when another tool builds the frontend. |
| `RunicApplicationFrontendCopyEnabled` | property | `true` | Mirror the frontend output to the web root. |
| `RunicApplicationFrontendDevSessionEnabled` | property | `true` | `false` makes a build ignore a running `dotnet runic dev` session, which otherwise skips the frontend build and keeps dev's development pages in the web root (since 0.20). |
| `RunicApplicationFrontendOutputDirectory` | property | `dist` | The frontend build's output, relative to the frontend directory. |
| `RunicApplicationFrontendWebRoot` | property | `www` | The web root below the build and publish output. |
| `RunicApplicationFrontendTranslations` | property | `false` | `true` mirrors the Runic Translations ESM of the project's catalog into the frontend before it builds, for a page that imports it without the Translations Vite plugin. Needs `RunicTranslationsEmitEsm=true`. |
| `RunicApplicationFrontendTranslationsDirectory` | property | `<frontend>/.runic/translations` | Where `RunicApplicationFrontendTranslations` mirrors the ESM. |
| `RunicApplicationFrontendDelivery` | property | `Files` | `Files` or `Embedded`. |
| `RunicApplicationHost` | property | the only host adapter referenced | `desktop` or `cswebui`, when a project references both. |
| `RunicApplicationFrontendCompositionType` | property | `<RootNamespace>.RunicViewsComposition` | The generated composition class. |
| `RunicApplicationFrontendModelAssembly` | property | the project itself | A separately built ViewModel assembly. |
| `RunicApplicationFrontendGenerationEnabled` | property | inferred | `true` requires Bridge generation, `false` turns it off. |
| `RunicApplicationFrontendDocumentationEnabled` | property | `true` | XML documentation for comments in the generated TypeScript. |
| `RunicApplicationFrontendWindowProject` | property | `false` | Marks the Window project that `dotnet runic dev` and `doctor` run. |
| `RunicApplicationFrontendBootstrap` | property (read-only) | set by the targets | `true` only in the bootstrap pass. |
| `RunicDevelopmentOnlyViews` | property | `auto` | `auto` leaves [development-only views and members](packaging.md#development-only-views-and-members) (`[RunicDevelopmentOnly]`) out of trimmed and NativeAOT publishes; `true` keeps them in every publish (since 0.22). Builds, `dotnet run` and untrimmed publishes always keep them. |
| `RunicBridgeBlobs` | property | `auto` | `auto` turns Bridge blobs and uploads (`blobs/1`) off in a trimmed or NativeAOT publish when no assembly's generated Bridges hold a `RunicBlob` or take a `RunicUpload`, so the executable leaves the blob store and the hosts' blob endpoints out (since 0.23); `true` keeps them in every publish. Builds, `dotnet run` and untrimmed publishes always keep them. |
| `RunicApplicationFrontendInput` | item | none | An input outside the frontend directory that rebuilds it. |
| `RunicApplicationFrontendBuildEnvironment` | item | none | An environment variable that changes the built frontend. |

## dotnet runic

[`dotnet runic dev`](../tools/dotnet-runic/README.md#project-properties) also reads
`RunicApplicationFrontendDirectory`, `RunicApplicationFrontendOutputDirectory` and
`RunicApplicationFrontendWebRoot`, and:

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicApplicationFrontendDevServerKind` | `angular` or `vite` from the frontend's files | The development server to start. |
| `RunicApplicationFrontendViteDevServerEntry` | the first `src/main.*`, or the SvelteKit root route | The module that confirms the Vite server. |
| `RunicApplicationFrontendViteConfiguration` | the frontend's `vite.config.*` | The Vite configuration. |
| `RunicApplicationFrontendDevServerDocument` | per development server | The HTML documents the server renders, separated by `;`. |
| `RunicApplicationFrontendDevWatchTarget` | none | An MSBuild target that watches the frontend without a development server. |

[`dotnet runic package`](../tools/dotnet-runic/README.md#package-the-application)
reads the SDK's `ApplicationId`, `ApplicationTitle`, `Version`, `Description`,
`PackageProjectUrl` and `PackageLicenseExpression`, and (see also the
[packaging guide](packaging.md#the-manifests)):

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicApplicationPublisher` | none | The MSIX `Publisher`; `--publisher` overrides it. |
| `RunicApplicationUrlSchemes` | none | The URL schemes the packages declare. |
| `RunicApplicationFileTypes` | none | The file types the packages declare. |
| `RunicApplicationWebLinkHosts` | none | The web link hosts the packages declare. |
| `RunicApplicationDBusActivatable` | none | D-Bus activation of the Linux packages. |
| `RunicPackageIcon` | `icons/icon.png` | The 512 px PNG icon, the application's only icon: the build derives the Windows `.ico` and the macOS development bundle's icon from it too ([the application icon](packaging.md#the-application-icon)). |
| `RunicPackageName` | the lower-cased assembly name | The deb and rpm package name. |
| `RunicPackagingDirectory` | `packaging/` | Manifest overrides. |
| `RunicMacSandbox` | `false` | Sign the macOS bundle for the App Sandbox. |
| `RunicMacApplicationCategory` | none | The macOS `LSApplicationCategoryType`. |
| `RunicMacFileTypeIdentifiers` | none | Uniform type identifiers of file types the application does not own. |
| `RunicMacFileTypeHandlerRank` | `Alternate` | The macOS handler rank of those types: `Alternate` or `Default`. |
| `RunicMacCameraUsageDescription` | none | `NSCameraUsageDescription` and the camera entitlement. |
| `RunicMacMicrophoneUsageDescription` | none | `NSMicrophoneUsageDescription` and the audio-input entitlement. |
| `RunicApplicationLocales` | none | The languages the packages declare, the first being the development language: `en;de;pt-BR`. See [Languages](packaging.md#languages). |

The `RunicApplicationLocale` item adds a language too, with the application's
name and description in it: `<RunicApplicationLocale Include="de" Name="Notizen" Description="Notizen für alle" />`.

## Runic.Assets

See [Embed a Vite build](../packages/dotnet/Runic.Assets/README.md#embed-a-vite-build).

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicAssetsDist` | none | The directory to pack and embed. |
| `RunicAssetsEmbeddedArchive` | none | An existing canonical archive to embed instead. |
| `RunicAssetsEntryPoint` | `index.html` | The archive's entry file. |
| `RunicAssetsDistExclude` | `runic-assets.zip` | Files to leave out; the value replaces the default. |
| `RunicAssetsDefaultExcludes` | `true` | `false` packs `.git` directories and `.env*` files too. |
| `RunicAssetsEmbeddedResourceName` | `Runic.Assets.StaticFiles` | The manifest resource name. |
| `RunicAssetsPackerPath` | the packer in the package | The asset packer the build runs ([`RAS2003`](diagnostics.md#ras2003)). |

## Runic.Application.Views.Desktop

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicDesktopRegionHost` | `true` | `false` for an application whose pages declare no [hosted regions](hosted-regions.md): the host no longer follows its pages' regions, and a trimmed or NativeAOT build leaves the region host out. Guests can still show their windows in native windows. Applies to every build. |

## Runic.Desktop

See [MSBuild properties](../packages/dotnet/Runic.Desktop/README.md#msbuild-properties).

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicDesktopServer` | the referenced server package: `Native` with `Runic.Desktop.NativeServer` (also when both are referenced), else `Kestrel` with `Runic.Desktop.Kestrel` | The presentation server: `Native`, `Kestrel`, or `None` for an executable that opens no Runic Desktop window. A trimmed or NativeAOT build keeps only the selected server, and none for `None`. Diagnostics [`RUNICDESK003`](diagnostics.md#runicdesk003), [`RUNICDESK004`](diagnostics.md#runicdesk004) and [`RUNICDESK007`](diagnostics.md#runicdesk007) report a wrong value or a missing or unused server package. |
| `RunicDesktopMinimalHost` | `false` | Build each surface's listener from an empty ASP.NET Core builder. |

## Runic.Desktop.NativeServer

See [NativeAOT and an application's own Rust library](../packages/dotnet/Runic.Desktop.NativeServer/README.md#nativeaot-and-an-applications-own-rust-library).

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicDesktopNativeServerLinkage` | `Static` | How a NativeAOT publish gets the server: `Static` links the static library of `Runic.Desktop.NativeServer.Static.<rid>` into the executable, `Shared` puts the shared library beside it, and `Application` leaves the server to the application's own Rust library, which links the `runic-server` crate. Other builds always use the shared library. |
| `RunicDesktopNativeServerLibraryDirectory` | none | A directory whose `librunic_server.so`, `.dylib` or `.a`, or `runic_server.dll` or `.lib`, replaces the package's, such as a library built from source ([Nix builder](../nix/README.md)). |

## Runic.Spelling

See [Build](../packages/dotnet/Runic.Spelling/README.md#build). Runic.Spelling is a preview package, outside the 1.0
promise.

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicSpellingBundledDictionaries` | `true` | `false` leaves the bundled English dictionaries out of the build and publish output (`dictionaries/`). |

## Runic.Platform and the privileged-action packages

See [Declare privileged actions](../packages/dotnet/Runic.Platform/README.md#declare-privileged-actions).

| Name | Kind | Default | Purpose |
| --- | --- | --- | --- |
| `RunicPrivilegedAction` | item | none | A privileged action. |
| `RunicPrivilegedCommand` | item | none | A command under one action. |
| `RunicPrivilegedActionsClassName` | property | `PrivilegedActions` | The generated class. |
| `RunicPrivilegedActionsNamespace` | property | `$(RootNamespace)` | Its namespace. |
| `RunicPrivilegedActionsReservedNames` | property (read-only) | `All`, `MacOSDaemon`, `WindowsElevation` | Names an action cannot have. |
| `RunicPrivilegedActionsDependsOn` | property | the platform packages' targets | The targets that run before the class is written. A platform package appends its own target, which depends on `RunicValidatePrivilegedActions`; with `RunicPrivilegedActionsReservedNames`, it is the protocol between Runic.Platform and the platform packages, not an application setting. |
| `RunicApplicationIconGenerationEnabled` | property | `true` | `false` turns off the icons the build derives from `RunicPackageIcon` (since 0.23): the `.ico` it uses as `ApplicationIcon` when the project sets none, the PNG beside the executable that a Runic Desktop window shows on macOS outside a bundle, and the macOS development bundle's `icon.icns`. A project with its own `ApplicationIcon` (or `Win32Resource`) keeps it either way. See [the application icon](packaging.md#the-application-icon). |
| `RunicPublishAllPlatformAssemblies` | property | `false` | `true` keeps every operating system's `Runic.Platform` providers and Linux-only libraries in a `dotnet publish -r` for a Windows, macOS or Linux RuntimeIdentifier. By default (since 0.22) such a publish, framework-dependent or self-contained without trimming, with or without ReadyToRun or single-file, ships only its own operating system's (`Runic.Desktop.Gtk4`, whose `WithGtk4()` runs on every operating system, stays; its GirCore bindings ship only for Linux), and code that calls another operating system's provider from a method that also runs on this one gets `FileNotFoundException`. Builds, `dotnet run`, publishes without `-r`, trimmed and NativeAOT publishes, and so every `dotnet runic package` output (NativeAOT), are not affected. |

[Runic.Platform.Linux.Polkit](../packages/dotnet/Runic.Platform.Linux.Polkit/README.md#declare-actions):

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicPolkitPolicyName` | `$(ApplicationId)`, else `$(AssemblyName)` | The policy file's name. |
| `RunicPolkitPolicyOutputPath` | `share/polkit-1/actions/` | Where the policy goes in the output. |
| `RunicPolkitPolicyCopyEnabled` | `true`, except for `win-*` and `osx-*` | Copy the policy to the output and publish directories. |
| `RunicApplicationInstallPrefix` | `/usr` | The install prefix an action's `Program` can name; the [Nix builder](../nix/README.md) sets it to `$out`. |

[Runic.Platform.MacOS](../packages/dotnet/Runic.Platform.MacOS/README.md#build):

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicMacPrivilegedDaemonLabel` | none | The launchd label and XPC Mach service name. |
| `RunicMacBundleIdentifier` | `$(ApplicationId)` | The application's bundle and code-signing identifier. |
| `RunicMacTeamIdentifier` | none | The signing Team ID. |
| `RunicMacPrivilegedDaemonProgram` | none | The daemon executable's path in the bundle. |
| `RunicMacPrivilegedDaemonIdentifier` | the label | The daemon's code-signing identifier. |
| `RunicMacLaunchDaemonOutputPath` | `Library/LaunchDaemons/` | Where the property list goes in the output. |
| `RunicMacLaunchDaemonPlistCopyEnabled` | `true` | Copy the property list to the output and publish directories. |
| `RunicMacPrivilegedDaemon` | `false` | `true` in the daemon's project. |
| `RunicMacPrivilegedDevelopmentSigning` | `false` | Accept development-signed code; local development only. |
| `RunicMacDevelopmentBundle` | `true` | On macOS, `dotnet run` starts the application from a development bundle ([details](../packages/dotnet/Runic.Platform.MacOS/README.md#development-bundle)). |

[Runic.Platform.Windows.Elevation](../packages/dotnet/Runic.Platform.Windows.Elevation/README.md#write-the-helper):

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicWindowsElevatedHelper` | `false` | `true` in the elevated helper's project. |

## Runic.Platform.Linux.DBus

See [Generate proxies and services](../packages/dotnet/Runic.Platform.Linux.DBus/README.md#generate-proxies-and-services).
These are metadata of an `AdditionalFiles` item, not properties.

| Metadata | Default | Purpose |
| --- | --- | --- |
| `RunicDBusMode` | required | `Proxy`, `Service` or `Proxy,Service`. |
| `RunicDBusNamespace` | required | The generated types' namespace. |
| `RunicDBusVisibility` | `internal` | `internal` or `public`. |
| `RunicDBusClassName` | derived from the interface | The generated class's name. |
