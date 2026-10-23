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
| `macos` | macOS | `<name>.app`, `<name>-<version>.dmg` | Drag the bundle to Applications |
| `flatpak` | Linux | `<id>.flatpak` and a repository | `flatpak install --user <id>.flatpak` |
| `appimage` | Linux | `<Name>-<version>-<arch>.AppImage` | `chmod +x` and run it |
| `deb` | Linux | `<package>_<version>_<arch>.deb` | `sudo apt install ./<file>.deb` |
| `rpm` | Linux, also Debian and Ubuntu | `<package>-<version>-1.<arch>.rpm` | `sudo dnf install ./<file>.rpm` |

Each target refuses to run on another operating system (`RAPPPKG1004`), and a
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
`codesign`, `lipo`, `iconutil` and `notarytool`. The command signs the bundle
with the hardened runtime and `macos/entitlements.plist`, and checks it with
`codesign --verify --strict`.

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
`--runtime osx-arm64,osx-x64` builds a universal executable for both Apple
silicon and Intel Macs.

Universal links for `RunicApplicationWebLinkHosts` need the
`com.apple.developer.associated-domains` entitlement and a provisioning
profile, which an ad-hoc signature cannot carry. Add both to your
`macos/entitlements.plist` override and embed the profile as
`Contents/embedded.provisionprofile` before you sign.

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
