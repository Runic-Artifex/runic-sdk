# MSBuild properties and items

Every MSBuild property and item that a Runic SDK package or tool reads from an
application's project, by package. Each row links to the section that describes
it in full; the defaults there are the reference. The
[release policy](../eng/release/README.md#what-counts-as-breaking) covers the
names on this page: a removed or renamed one, or a changed default that breaks a
build, is a breaking change.

Not on this page, and not settings: names that start with `_Runic`, targets named
`RunicViews*`, the Bridge generator's command line and the files it writes under
`obj/`. A renamed property keeps its old name working with a warning; the former
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
| `RunicApplicationFrontendBuildCommand` | property | the package manager's `run build` | The production frontend build. |
| `RunicApplicationFrontendInstallCommand` | property | the package manager's locked install | The install before the build. |
| `RunicApplicationFrontendAllowUnlockedInstall` | property | `false` | Install without a lock file. |
| `RunicApplicationFrontendInstallEnabled` | property | `true` | `false` when a workspace install owns the packages. |
| `RunicApplicationFrontendNodeModulesDirectory` | property | none | A `node_modules` installed elsewhere, such as by the Nix builder. |
| `RunicApplicationFrontendBuildEnabled` | property | `true` | `false` when another tool builds the frontend. |
| `RunicApplicationFrontendCopyEnabled` | property | `true` | Mirror the frontend output to the web root. |
| `RunicApplicationFrontendOutputDirectory` | property | `dist` | The frontend build's output, relative to the frontend directory. |
| `RunicApplicationFrontendWebRoot` | property | `www` | The web root below the build and publish output. |
| `RunicApplicationFrontendDelivery` | property | `Files` | `Files` or `Embedded`. |
| `RunicApplicationHost` | property | the only host adapter referenced | `desktop` or `cswebui`, when a project references both. |
| `RunicApplicationFrontendCompositionType` | property | `<RootNamespace>.RunicViewsComposition` | The generated composition class. |
| `RunicApplicationFrontendModelAssembly` | property | the project itself | A separately built ViewModel assembly. |
| `RunicApplicationFrontendGenerationEnabled` | property | inferred | `true` requires Bridge generation, `false` turns it off. |
| `RunicApplicationFrontendDocumentationEnabled` | property | `true` | XML documentation for comments in the generated TypeScript. |
| `RunicApplicationFrontendWindowProject` | property | `false` | Marks the Window project that `dotnet runic dev` and `doctor` run. |
| `RunicApplicationFrontendBootstrap` | property (read-only) | set by the targets | `true` only in the bootstrap pass. |
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
| `RunicPackageIcon` | `icons/icon.png` | The 512 px PNG icon. |
| `RunicPackageName` | the lower-cased assembly name | The deb and rpm package name. |
| `RunicPackagingDirectory` | `packaging/` | Manifest overrides. |
| `RunicMacSandbox` | `false` | Sign the macOS bundle for the App Sandbox. |
| `RunicMacApplicationCategory` | none | The macOS `LSApplicationCategoryType`. |
| `RunicMacFileTypeIdentifiers` | none | Uniform type identifiers of file types the application does not own. |
| `RunicMacFileTypeHandlerRank` | `Alternate` | The macOS handler rank of those types: `Alternate` or `Default`. |
| `RunicMacCameraUsageDescription` | none | `NSCameraUsageDescription` and the camera entitlement. |
| `RunicMacMicrophoneUsageDescription` | none | `NSMicrophoneUsageDescription` and the audio-input entitlement. |

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

## Runic.Desktop

See [MSBuild properties](../packages/dotnet/Runic.Desktop/README.md#msbuild-properties).

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicDesktopMinimalHost` | `false` | Build each surface's listener from an empty ASP.NET Core builder. |

## Runic.Platform and the privileged-action packages

See [Declare privileged actions](../packages/dotnet/Runic.Platform/README.md#declare-privileged-actions).

| Name | Kind | Default | Purpose |
| --- | --- | --- | --- |
| `RunicPrivilegedAction` | item | none | A privileged action. |
| `RunicPrivilegedCommand` | item | none | A command under one action. |
| `RunicPrivilegedActionsClassName` | property | `PrivilegedActions` | The generated class. |
| `RunicPrivilegedActionsNamespace` | property | `$(RootNamespace)` | Its namespace. |
| `RunicPrivilegedActionsReservedNames` | property (read-only) | `All`, `MacOSDaemon`, `WindowsElevation` | Names an action cannot have. |

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
