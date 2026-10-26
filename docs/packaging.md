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
try.

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
