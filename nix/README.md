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
      url = "github:Runic-Artifex/runic-sdk/v0.9.1";
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
| `nugetLockFiles` | `[ ]` | `packages.lock.json` files whose packages restore the project: its own, and those of projects it references. `{ linux-x64 = [ ... ]; linux-arm64 = [ ... ]; }` for several runtimes; see [Several runtimes](#several-runtimes). |
| `nugetDeps` | `null` | More NuGet packages: a `deps.json`, a package derivation or a list of them, as `buildDotnetModule` takes. |
| `nugetLockedMode` | `true` with lock files | Restore with `RestoreLockedMode`, so a lock file that no longer matches the project fails the build. |
| `dotnetTools` | `null` | Local .NET tools the build runs, restored offline from their manifest: `manifest` (relative to `src`, or a path; default `.config/dotnet-tools.json` beside the project) and `hashes`. See [Local .NET tools](#local-net-tools). |
| `frontend` | `null` | The frontend that the Runic MSBuild targets build: `directory` (relative to `src`; default `Frontend` beside the project), `packageManager` (`npm`, `pnpm` or `bun`, required), `hash`, `pnpmFetcherVersion` (default 4), or `nodeModules`. See below. |
| `host` | `"desktop"` | `desktop` (GTK 3, WebKitGTK 4.1), `desktop-gtk4` (GTK 4, WebKitGTK 6.0), `cswebui`, or `null`: which libraries the host loads at run time. |
| `withMedia` | `false` | GStreamer plugins for audio and video in WebKitGTK. |
| `withSpeechSynthesis` | `true` | WebKitGTK with speech synthesis. `false` uses one built without it; see [Without speech synthesis](#without-speech-synthesis). |
| `nativeAot` | `false` | Publish `projectFile` with NativeAOT; see [NativeAOT](#nativeaot). Self-contained either way when set. |
| `selfContainedBuild` | `nativeAot` | A self-contained JIT build. |
| `testRunner` | `"VSTest"` | How `doCheck` runs `testProjectFile`: `"VSTest"` with nixpkgs' `dotnet test`, or `"Microsoft.Testing.Platform"`; see [Tests](#tests). |
| `completions` | `null` | Shell completions the program prints, installed for bash, zsh and fish: `program` (default `meta.mainProgram`), `command` (default `[ "completion" ]`) and `shells`. See [Shell completions](#shell-completions). |
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

A NativeAOT restore adds the compiler packages, `Microsoft.DotNet.ILCompiler`
and `Microsoft.NET.ILLink.Tasks`, so its graph differs from the JIT one. Whether
the runtime's section also lists `runtime.linux-x64.Microsoft.DotNet.ILCompiler`
depends on where restore found `Microsoft.DotNet.ILCompiler`: nuget.org's
package adds it, while the build of it that the .NET SDK in nixpkgs ships with
(its `library-packs`) uses the SDK's own compiler pack and adds nothing there.
Both locks build, since the SDK provides these packages. Keep a second lock
file for the NativeAOT graph, as the template does:

```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>
<NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">packages.aot.lock.json</NuGetLockFilePath>
```

and write it with `dotnet restore -p:PublishAot=true`. The compiler packages
come from the .NET SDK, so they are not downloaded. When several lock files
record different hashes for the same package and version, evaluation fails.

#### Several runtimes

The locked restore is for one runtime identifier alone (`dotnet restore
--runtime`), and fails with `NU1004` when the lock file lists other runtimes
too, so evaluation rejects such a lock. To build for `x86_64-linux` and
`aarch64-linux`, keep one lock file per runtime and project, chosen by
`RuntimeIdentifiers`, and pass them keyed by runtime identifier; the build takes
those of its system's runtime:

```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>
<NuGetLockFilePath Condition="'$(RuntimeIdentifiers)' == 'linux-arm64'">packages.linux-arm64.lock.json</NuGetLockFilePath>
```

```sh
dotnet restore                                    # packages.lock.json
dotnet restore -p:RuntimeIdentifiers=linux-arm64  # packages.linux-arm64.lock.json
```

```nix
nugetLockFiles = {
  linux-x64 = [ ./MyApp/packages.lock.json ];
  linux-arm64 = [ ./MyApp/packages.linux-arm64.lock.json ];
};
```

The restore for another runtime runs on any system. With NativeAOT, add a
condition on `PublishAot` for each runtime's NativeAOT lock file, such as
`packages.linux-arm64.aot.lock.json`, and restore it with
`dotnet restore -p:PublishAot=true -p:RuntimeIdentifiers=linux-arm64`.

Packages from another feed, or a project without lock files, use nixpkgs'
`deps.json`: set `nugetDeps = ./deps.json`, and generate it with
`nix build .#default.fetch-deps && ./result`.

### Local .NET tools

A build step that runs a local tool from `dotnet-tools.json`, such as the
[Translations Vite plugin](https://github.com/Runic-Artifex/runic-translations-sdk),
which runs `dotnet tool run runic-translations`, needs the tool restored, and
`dotnet tool restore` downloads it. `dotnetTools` restores the tools of a
manifest offline instead. Each tool package is a fixed-output download from
nuget.org by the hash of its `.nupkg`; start each with `lib.fakeHash` and copy
the hash that `nix build` reports:

```nix
dotnetTools = {
  manifest = "MyApp/.config/dotnet-tools.json";
  hashes.dotnet-runic-translations = "sha256-/sG0ngRIx6fh0LLQzBNvpCk6N1kYB1NYhhKpI6NjDPY=";
};
```

The manifest is read at evaluation, and `src` must include it. A relative
`manifest` is read from `src` when it is a path (`src = ./.`), and from the
unfiltered source when `src` is filtered by `lib.fileset.toSource`,
`lib.cleanSource` or `lib.cleanSourceWith`, so a read-only evaluation such as
`nix flake check --no-build --all-systems` works on an empty store. For any
other `src`, such as one that `builtins.path { filter; }` filters or a
derivation, set `manifest` to a path, which is read and restored from as it is:

```nix
dotnetTools = {
  manifest = ./MyApp/.config/dotnet-tools.json;
  hashes.dotnet-runic-translations = "sha256-/sG0ngRIx6fh0LLQzBNvpCk6N1kYB1NYhhKpI6NjDPY=";
};
```

A relative `manifest` of such a `src` is read from its store path, which a
read-only evaluation cannot do before a writable one has added it. Every tool
in the manifest needs a hash, and a new tool version a new one. The build runs `dotnet tool restore --tool-manifest`
before the restore of the projects; `dotnet tool run` then finds the manifest
from the directory it runs in, as during development. A manifest at the root
of `src` needs `dotnetTools` as well: nixpkgs restores its tools by itself, from
packages it cannot download.

### Frontend packages

The frontend's packages are installed into a separate derivation, from the
frontend's `package.json` and lock file only, so changing the frontend's
sources does not download them again. The build links the frontend's
`node_modules` to it and passes
`-p:RunicApplicationFrontendNodeModulesDirectory=<it>/node_modules`, so the
Runic targets build the frontend without installing.

| Package manager | How the packages are fetched | `hash` |
| --- | --- | --- |
| npm | Without a hash, nixpkgs' `importNpmLock` fetches each package of `package-lock.json` by its integrity; this reads `package.json` and `package-lock.json` at evaluation, as `dotnetTools` reads its manifest: from `src` when it is a path, and from the unfiltered source when `src` is filtered by `lib.fileset.toSource`, `lib.cleanSource` or `lib.cleanSourceWith`. Any other `src`, such as one that `builtins.path { filter; }` filters, needs a hash for a read-only evaluation (`nix flake check --no-build`). With a hash, `fetchNpmDeps` fetches them as one download. | optional |
| pnpm | `fetchPnpmDeps` (fetcher version 4), then `pnpmConfigHook` installs offline. | required |
| Bun | `bun install --frozen-lockfile --ignore-scripts` in a fixed-output derivation; a second derivation patches the scripts' interpreters, which a fixed-output derivation cannot reference. | required |

A required hash starts as `lib.fakeHash`. The first `nix build` fails and
reports the real hash; replace it, and update it whenever the lock file
changes. Nix names the download by its hash, so a changed lock file with the
previous hash would get the previous download. The npm and Bun builds compare
the lock file the download came from with the frontend's and fail when they
differ; pnpm installs from the frontend's lock file and fails when the download
lacks a package.

Bun installs only one system's optional packages, such as a bundler's native
binary, so its hash can differ per system. `hash` then takes one per system:

```nix
frontend = {
  packageManager = "bun";
  hash = {
    x86_64-linux = "sha256-...";
    aarch64-linux = lib.fakeHash;
  };
};
```

The install is told the system's OS and CPU (`bun install --os=linux
--cpu=arm64`), so any system can compute another's hash:
`nix build .#default.frontendNodeModules.fetchedFor.aarch64-linux` installs the
`aarch64-linux` packages and reports their hash. Nix names the download by its
hash alone, so a build on `aarch64-linux` uses the same download.

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

### Shell completions

A command-line application prints its own completion scripts: Runic.CommandLine
has the `completion bash|zsh|fish` command, and `--completions <shell>` when the
catalog declares `CommandCatalogBuilder.CompletionOption`. `completions` runs the
installed program after the build, through its wrapper, once per shell, and
installs the scripts where the shells and NixOS look for them
(`share/bash-completion/completions`, `share/zsh/site-functions`,
`share/fish/vendor_completions.d`):

```nix
meta.mainProgram = "my-app";
completions = { };                                # my-app completion bash, zsh, fish
completions.command = [ "--completions" ];        # my-app --completions bash, ...
```

The program runs with an empty temporary home (`HOME` and the XDG
directories). Export anything else it needs in `preFixup`. A script that is
empty fails the build. A cross build cannot run the program and installs no
completions.

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

### Without speech synthesis

WebKitGTK implements the Web Speech API's `speechSynthesis` with flite, which
it links together with the audio libraries only flite uses. A page that never
speaks, such as a dashboard, does not need them. Screen readers such as Orca
speak through their own speech service, not through WebKitGTK, so they are not
affected. With `withSpeechSynthesis = false` the wrapper loads a WebKitGTK
configured with `-DENABLE_SPEECH_SYNTHESIS=OFF` and without flite, for both
`desktop` and `desktop-gtk4`. With the nixpkgs of this flake's lock, WebKitGTK
4.1's closure loses 72.6 MiB of its 839.5 MiB: flite (60.5 MiB), PulseAudio,
libsndfile, FFTW and their codecs. With `withMedia`, GStreamer's plugins still
bring PulseAudio. `speechSynthesis` is then missing in the page.

nixpkgs' binary cache has no such WebKitGTK, so the first build compiles it from
source, which takes hours and needs the `big-parallel` system feature. Build it
once on a machine or cache of your own, for example with a binary cache that your
CI fills, rather than on every user's machine. The default keeps nixpkgs'
WebKitGTK, which the cache has.

### Privileged actions

`Runic.Platform.Linux.Polkit` writes the policy of the application's
`RunicPrivilegedAction` items to `share/polkit-1/actions` in the publish
directory; the builder installs it in `$out/share/polkit-1/actions`. The build
exports `RunicApplicationInstallPrefix` as `$out`, so the policy can name the
installed program (other builds default it to `/usr`):

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

### Tests

With `doCheck = true`, the build runs the tests of `testProjectFile` (a path
relative to `src`, or a list of them) after building them. Their
`packages.lock.json` belongs in `nugetLockFiles`, with the runtime's section
like the application's.

nixpkgs runs them with `dotnet test` and VSTest options such as `--logger`.
A Microsoft.Testing.Platform test application, as TUnit builds and MSTest and
xUnit.net v3 build with their own runner, rejects them, and `dotnet test` uses
Microsoft.Testing.Platform only when the `global.json` of the directory it runs
in selects it. Set `testRunner = "Microsoft.Testing.Platform"`: the build then
runs each test application it built, with `dotnet <TestProject>.dll`, and fails
when a test fails or none ran. `dotnetTestFlags` are the test application's
options, such as TUnit's `--treenode-filter`; `testFilters` and `disabledTests`
are VSTest filters and fail evaluation with it.

```nix
testProjectFile = "MyApp.Tests/MyApp.Tests.csproj";
nugetLockFiles = [ ./MyApp/packages.lock.json ./MyApp.Tests/packages.lock.json ];
doCheck = true;
testRunner = "Microsoft.Testing.Platform";
```

`dotnet new runic-app --nix --tests` creates such a test project, and its
`nix flake check` runs the tests.

### NativeAOT

`nativeAot = true` publishes `projectFile` with NativeAOT for the system's
runtime identifier, with Clang and zlib from nixpkgs and the NativeAOT packages
of the .NET SDK. It sets `PublishAot` for that project alone, as if its project
file set it, through `CustomAfterDirectoryBuildProps`, so the project sees it
in its own body, for example to choose `packages.aot.lock.json`. The projects it
references and `testProjectFile` keep their own settings: tests are restored
with their usual lock file and run under the JIT. A project that sets
`PublishAot` itself, or a `dotnetFlags` entry that sets
`CustomAfterDirectoryBuildProps`, overrides it.

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
in a shell without them. `dotnet-sdk` (default the .NET 10 SDK) and `nodejs`
(default Node 24) select the toolchain: pass the ones an existing shell or the
package uses, such as `nodejs = pkgs.nodejs_22;`, so that the shell and the
build agree; `buildRunicApplication` takes the same two arguments. Other
`mkShell` arguments pass through.

## Tests in the SDK

- `nix flake check` builds the status-icon example under JIT and NativeAOT, the
  privileged-actions smoke with its policy, and evaluates the Home Manager
  module. Its `builder-*` checks build small consumers from
  `tests/fixtures/nix`: `builder-test-runner` runs a TUnit project with
  `testRunner = "Microsoft.Testing.Platform"`, and `builder-dotnet-tools`
  restores a local tool from a manifest below the source's root with
  `dotnetTools`; `builder-dotnet-tools-filtered-source` does so from a
  `lib.fileset.toSource` source, and `builder-dotnet-tools-manifest-path` with
  `manifest` as a path and a source that `builtins.path` filters. The Nix
  workflow runs `nix flake check --no-build --all-systems` first, a read-only
  evaluation on an empty store, which fails when a builder reads a file from a
  store path that only a writable evaluation adds.
- The `builder-completions` flake check installs the bash, zsh and fish
  completions of a Runic.CommandLine program with `completions`.
- `nix build .#packaging-nixos -L` runs a NixOS VM (KVM) that installs the
  NativeAOT status-icon example and the smoke through the NixOS module. It
  checks the desktop entry and icon, that the user service starts with an X
  session and a second launch reaches it, that `pkaction` lists the policy with
  the package's program, and that pkexec runs the action through the wrapper.
- `tests/templates/Test-NixTemplate.sh candidates artifacts/packages npm pnpm bun aot`
  creates `--nix` projects from the template packed from this checkout
  (`bun eng/run.mjs pack`) and builds their flakes with this checkout's builder
  against the packed NuGet and npm packages. Their locked Runic NuGet packages
  are added to the store as the fixed-output paths the builder would download,
  and a loopback registry serves the npm archives to the sandbox's fixed-output
  downloads. Pull requests and main run this mode, so a change to API that the
  template uses is tested before it is published.
  `tests/templates/Test-NixTemplate.sh published <version> npm pnpm bun aot`
  creates the projects from the published template instead, with the packages
  from the registries and the builder of the `v<version>` tag, as a user's
  `dotnet new runic-app --nix` does. The Nix workflow runs it after Publish
  preview succeeds, weekly, and on a manual run with `packages: published`.
  In both modes the npm project has the test project (`--tests`), whose tests
  both of its packages run. For npm it also checks that a lock without
  `linux-x64` and a stale lock fail. With
  `RUNIC_NIX_TEMPLATE_WINDOW=1` each application opens its window under Xvfb
  and must load GTK and WebKitGTK from the store.
- The `builder-locks` flake check requires that a Bun frontend whose lock
  changed without its hash fails, that another system's Bun install builds,
  that an npm frontend without a hash installs from a `lib.fileset.toSource`
  source, and that lock files are chosen and checked per runtime identifier.
