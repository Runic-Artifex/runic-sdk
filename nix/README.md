# Package a Runic application with Nix

The SDK's flake exports a builder for Runic applications, a development shell,
and NixOS and Home Manager modules that install them. The builder wraps nixpkgs'
`buildDotnetModule`: it restores NuGet packages and the frontend's packages from
lock files, builds in the Nix sandbox without network access, and installs the
desktop entry, icons and Polkit policy.

`dotnet new runic-app --nix` creates a project with a working `flake.nix`; see
[the template's guide](../tools/Runic.Application.Templates/content/runic-app/README.md#build-it-with-nix).
For working on the SDK itself, see the
[NixOS development guide](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/nixos-development.md).

The builder is verified on `x86_64-linux`, under JIT and NativeAOT.

## A flake for an application

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    runic-sdk = {
      url = "github:Runic-Artifex/runic-sdk/v0.8.0-preview.2";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs = { self, nixpkgs, runic-sdk }:
    let
      pkgs = nixpkgs.legacyPackages.x86_64-linux;
    in
    {
      packages.x86_64-linux.default = runic-sdk.lib.buildRunicApplication pkgs {
        pname = "my-app";
        version = "1.0.0";
        src = ./.;
        projectFile = "MyApp/MyApp.csproj";
        nugetLockFiles = [ ./MyApp/packages.lock.json ];
        frontend = {
          directory = "MyApp/Frontend";
          packageManager = "npm";
        };
        nativeAot = true;
        executables = [ "MyApp" ];
        desktopItems = [
          (pkgs.makeDesktopItem {
            name = "org.example.MyApp";
            desktopName = "My App";
            exec = "MyApp";
            icon = "org.example.MyApp";
            categories = [ "Utility" ];
          })
        ];
        icons.scalable = ./MyApp/icon.svg;
        iconName = "org.example.MyApp";
        meta.mainProgram = "MyApp";
      };
    };
}
```

Every function of `runic-sdk.lib` takes the consumer's nixpkgs first.
`overlays.default` adds `pkgs.buildRunicApplication` for package files that
`callPackage` loads.

## buildRunicApplication

Arguments that `buildDotnetModule` also takes (`dotnetFlags`, `runtimeDeps`,
`makeWrapperArgs`, `preConfigure`, `postInstall`, `meta` and so on) pass through.

| Argument | Default | Meaning |
| --- | --- | --- |
| `pname`, `version`, `src` | required | As for `buildDotnetModule`. |
| `projectFile` | required | The application's project, relative to `src`. |
| `nugetLockFiles` | `[ ]` | `packages.lock.json` files whose packages restore the project: its own, and those of projects it references. |
| `nugetDeps` | `null` | More NuGet packages: a `deps.json`, a package derivation or a list of them, as `buildDotnetModule` takes. |
| `nugetLockedMode` | `true` with lock files | Restore with `RestoreLockedMode`, so a lock file that no longer matches the project fails the build. |
| `frontend` | `null` | The frontend that the Runic MSBuild targets build: `directory` (relative to `src`; default `Frontend` beside the project), `packageManager` (`npm`, `pnpm` or `bun`, required), `hash`, `pnpmFetcherVersion` (default 4), or `nodeModules`. See below. |
| `host` | `"desktop"` | `desktop` (GTK 3, WebKitGTK 4.1), `desktop-gtk4` (GTK 4, WebKitGTK 6.0), `cswebui`, or `null`: which libraries the host loads at run time. |
| `withMedia` | `false` | GStreamer plugins for audio and video in WebKitGTK. |
| `nativeAot` | `false` | Publish with NativeAOT. Self-contained either way when set. |
| `selfContainedBuild` | `nativeAot` | A self-contained JIT build. |
| `desktopItems` | `[ ]` | `makeDesktopItem` results, installed in `$out/share/applications`. |
| `icons` | `{ }` | `{ scalable = ./app.svg; "256x256" = ./app.png; }`, installed as `$out/share/icons/hicolor/<size>/apps/<iconName>.<svg or png>`. |
| `iconName` | `pname` | The icon's name, which the desktop entry's `icon` refers to. |
| `dotnet-sdk` | .NET 10 SDK | The SDK; the runtime is its ASP.NET Core runtime, which Runic's hosts serve the page with. |
| `nodejs` | Node 24 | Node for the frontend's install and build. |

The package has `passthru.frontendNodeModules`, the frontend's installed
dependencies, for a development shell or another build.

### NuGet packages

`nugetLockFiles` reads committed `packages.lock.json` files at evaluation time,
so no second lock file has to be generated. Set
`<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` in the project
and commit the file that `dotnet restore` writes. Each locked package is a
fixed-output download from nuget.org. A lock file records the package's hash
without its signature, which nuget.org adds to every package; the download
removes the signature and checks that hash, and restore accepts the unsigned
package. Packages that the .NET SDK in nixpkgs provides, such as
`Microsoft.NET.ILLink.Tasks` and the NativeAOT compiler, are not downloaded.

`buildDotnetModule` restores for the system's runtime identifier
(`linux-x64`), so each lock file needs that runtime's section: set
`<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>` in the project. Its
packages, including those a package's `runtime.json` adds for the runtime, are
then locked and fetched too. The build restores in locked mode: a lock file
without the section fails at evaluation with a message naming it, and one that
no longer matches the project's references fails the restore with `NU1004`;
restore again and commit the lock. `nugetLockedMode = false` restores without
that check, re-resolving whatever the lock lacks from the fetched packages. nixpkgs installs NuGet packages without a
content hash, which a locked restore rejects, so the build links each locked
package into the restore's packages folder with the hash its lock records.

A NativeAOT restore adds the compiler packages (`Microsoft.DotNet.ILCompiler`,
and `runtime.linux-x64.Microsoft.DotNet.ILCompiler` in the runtime's section),
so its graph differs from the JIT one. Keep a second lock file for it, as the
template does:

```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>
<NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">packages.aot.lock.json</NuGetLockFilePath>
```

and write it with `dotnet restore -p:PublishAot=true`. The compiler packages
come from the .NET SDK, so they are not downloaded. When several lock files
record different hashes for the same package and version, evaluation fails.

Packages from another feed, or a project without lock files, use nixpkgs'
`deps.json`: set `nugetDeps = ./deps.json`, and generate it with
`nix build .#default.fetch-deps && ./result`.

### Frontend packages

The frontend's packages are installed into a separate derivation, from the
frontend's `package.json` and lock file only, so changing the frontend's
sources does not download them again. The build links the frontend's
`node_modules` to it and passes
`-p:RunicApplicationFrontendNodeModulesDirectory=<it>/node_modules`, so the
Runic targets build the frontend without installing.

| Package manager | How the packages are fetched | `hash` |
| --- | --- | --- |
| npm | Without a hash, nixpkgs' `importNpmLock` fetches each package of `package-lock.json` by its integrity; this reads the lock file at evaluation, so `src` must be a local path such as the flake's own source. With a hash, `fetchNpmDeps` fetches them as one download. | optional |
| pnpm | `fetchPnpmDeps` (fetcher version 4), then `pnpmConfigHook` installs offline. | required |
| Bun | `bun install --frozen-lockfile --ignore-scripts` in a fixed-output derivation; a second derivation patches the scripts' interpreters, which a fixed-output derivation cannot reference. | required |

A required hash starts as `lib.fakeHash`. The first `nix build` fails and
reports the real hash; replace it, and update it whenever the lock file
changes. Bun installs only the current platform's optional packages, so its hash
differs per system.

The build runs `npm run build`, `pnpm run build` or `bun run --bun build` as
usual, with pnpm told not to fetch the version that `package.json` names and
not to install again before the script. pnpm refuses packages younger than its
`minimumReleaseAge` (a day) when it fetches; set it in `pnpm-workspace.yaml` if
a just-released version must be used.

Only the frontend directory's `package.json`, lock file, `.npmrc` and
`pnpm-workspace.yaml` are fetched from. A workspace whose members have their own
`package.json` needs `frontend.nodeModules`, which replaces the fetch with any
derivation that has `node_modules` at its root; `null` builds a frontend
without dependencies.

### Run time

GTK, WebKitGTK and GLib are loaded with `dlopen`, so they are run-time
dependencies, never bundled. The program in `bin` is a binary wrapper (from
`makeBinaryWrapper`, so no shell runs) that sets `LD_LIBRARY_PATH` to them,
`XDG_DATA_DIRS` to their GSettings schemas, `GIO_EXTRA_MODULES` to
glib-networking, and, for JIT builds, `DOTNET_ROOT`. Run the application
through the wrapper: the program behind it in `lib/<pname>` finds no GTK or
WebKitGTK, and Runic Desktop then reports `webkitgtk-runtime-missing` and opens
a browser instead. A wrapper that needs `--run` (`useDotnetFromEnv`) is a shell
script instead, which is not fit to be a privileged action's program.

A `cswebui` application gets none of them: CS-WebUI initialises GTK whenever it
can load it, even when it serves without a display, and opens an installed
browser instead.

The wrapper also sets `RUNIC_APPLICATION_EXECUTABLE` to itself. Login startup
(`IDesktopLoginStartup`) then writes the wrapper into the autostart entry rather
than the program behind it. The application reads the variable when it starts
and removes it from its environment, and trusts it only for a wrapper in the
same package (`<prefix>/bin/<name>`, with the program below `<prefix>`). That path names one version of the package; on
NixOS or Home Manager, the modules' user service starts whatever version is
installed.

### Privileged actions

`Runic.Platform.Linux.Polkit` writes the policy of the application's
`RunicPrivilegedAction` items to `share/polkit-1/actions` in the publish
directory; the builder installs it in `$out/share/polkit-1/actions`. The build
exports `RunicApplicationInstallPrefix` as `$out`, so the policy can name the
installed program:

```xml
<RunicPrivilegedAction Include="org.example.app.activate"
                       Program="$(RunicApplicationInstallPrefix)/bin/MyApp"
                       Verb="activate" ... />
```

pkexec runs that binary wrapper as root, which sets the run-time environment
from fixed store paths and starts the program; this works for JIT builds too,
whose program needs `DOTNET_ROOT`. The build fails when a policy names a program
outside the package, or one that is not an executable regular file at its real
path.

### NativeAOT

`nativeAot = true` publishes with `-p:PublishAot=true` for the system's runtime
identifier, with Clang and zlib from nixpkgs and the NativeAOT packages of the
.NET SDK.

## NixOS and Home Manager modules

```nix
{
  imports = [ runic-sdk.nixosModules.runic-applications ];
  programs.runic.applications.my-app = {
    package = my-app.packages.x86_64-linux.default;
    userService = {
      enable = true;
      arguments = [ "--tray" ];
    };
  };
}
```

| Option | Default | Meaning |
| --- | --- | --- |
| `enable` | `true` | Install the application. |
| `package` | required | The application. Its desktop entries, icons and Polkit policy are installed with it. |
| `executable` | `lib.getExe package` | The program the user service starts. |
| `userService.enable` | `false` | A systemd user service that starts the application with the graphical session (`graphical-session.target`), for an application that runs in the background, such as one with a status icon. |
| `userService.arguments` | `[ ]` | Its arguments, each passed unchanged. |
| `userService.environment` | `{ }` | Its environment variables. |
| `userService.description` | `"<name> in the background"` | The unit's description. |

The NixOS module adds the package to `environment.systemPackages`, which links
its policy into the actions directory that polkitd reads, and enables Polkit and
the setuid pkexec wrapper. The Home Manager module
(`runic-sdk.homeManagerModules.runic-applications`) has the same options and
adds the package to `home.packages`. polkitd reads only the system's policies,
so an application with privileged actions also needs the package in the system
profile.

A launch from the desktop entry while the user service runs reaches the running
instance when the application claims a single instance
(`ApplicationInstance.ClaimAsync`).

## Development shell

```nix
devShells.x86_64-linux.default = runic-sdk.lib.mkDevShell pkgs {
  hosts = [ "desktop" ];
  packageManagers = [ "bun" ];
};
```

The shell has the .NET SDK, Node, the package managers, Clang and zlib for
NativeAOT, and the run-time environment of the hosts: for `desktop` and
`desktop-gtk4`, GTK and WebKitGTK on `LD_LIBRARY_PATH` with their schemas and
GIO modules. `cswebui` and `browserTests = true` add Chromium as
`PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH`; `cswebui` also sets
`CSWEBUI_BROWSER_PATH`, so CS-WebUI windows open in that Chromium and fall back
to no other browser. (WebUI itself reads no browser variable, and never read
`WEBUI_BROWSER_PATH`.) A shell for both
Runic Desktop and CS-WebUI has the Desktop libraries, so run headless CS-WebUI
in a shell without them. Other `mkShell` arguments pass through.

## Tests in the SDK

- `nix flake check` builds the status-icon example under JIT and NativeAOT, the
  privileged-actions smoke with its policy, and evaluates the Home Manager
  module.
- `nix build .#packaging-nixos -L` runs a NixOS VM (KVM) that installs the
  NativeAOT status-icon example and the smoke through the NixOS module. It
  checks the desktop entry and icon, that the user service starts with an X
  session and a second launch reaches it, that `pkaction` lists the policy with
  the package's program, and that pkexec runs the action through the wrapper.
- `tests/templates/Test-NixTemplate.sh <published-version> npm pnpm bun aot`
  creates `--nix` projects from this checkout's template against a published
  Runic version and builds their flakes with this checkout's builder. For npm
  it also checks that a lock without `linux-x64` and a stale lock fail. With
  `RUNIC_NIX_TEMPLATE_WINDOW=1` each application opens its window under Xvfb
  and must load GTK and WebKitGTK from the store.
