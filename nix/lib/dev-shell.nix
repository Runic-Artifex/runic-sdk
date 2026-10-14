# mkDevShell: a development shell for a Runic application, with the .NET SDK, Node, the frontend's package
# manager, the NativeAOT toolchain, and the run-time environment of the hosts the application uses.
#
# Runic Desktop (desktop, desktop-gtk4) needs GTK and WebKitGTK on LD_LIBRARY_PATH, with their GSettings schemas
# on XDG_DATA_DIRS and glib-networking's GIO modules. CS-WebUI (cswebui) gets a browser instead and keeps GTK off
# the library path: it initialises GTK whenever it can load it, even when it serves without a display. A shell
# for both hosts therefore has the Desktop libraries, and headless CS-WebUI runs belong in a shell without them.
{
  lib,
  callPackage,
  mkShell,
  dotnetCorePackages,
  nodejs_24,
  pnpm,
  bun,
  clang,
  zlib,
  chromium,
}:
{
  hosts ? [ "desktop" ],
  packageManagers ? [
    "npm"
    "pnpm"
    "bun"
  ],
  # chromium for CS-WebUI and for headless browser tests (PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH).
  browserTests ? false,
  dotnet-sdk ? dotnetCorePackages.sdk_10_0,
  nodejs ? nodejs_24,
  packages ? [ ],
  shellHook ? "",
  ...
}@args:
let
  runtimes = map (callPackage ./hosts.nix { }) hosts;
  libraries = lib.unique (lib.concatMap (runtime: runtime.libraries) runtimes);
  inputs = lib.unique (lib.concatMap (runtime: runtime.buildInputs) runtimes);
  browser = browserTests || lib.elem "cswebui" hosts;
in
mkShell (
  removeAttrs args [
    "hosts"
    "packageManagers"
    "browserTests"
    "dotnet-sdk"
    "nodejs"
  ]
  // {
    packages =
      packages
      ++ [
        dotnet-sdk
        nodejs
        clang
        zlib
      ]
      ++ lib.optional (lib.elem "pnpm" packageManagers) pnpm
      ++ lib.optional (lib.elem "bun" packageManagers) bun
      ++ lib.optional browser chromium;

    DOTNET_ROOT = "${dotnet-sdk}/share/dotnet";
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";

    shellHook =
      lib.optionalString (libraries != [ ]) ''
        export LD_LIBRARY_PATH="${lib.makeLibraryPath libraries}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
        export GIO_EXTRA_MODULES="${lib.makeSearchPath "lib/gio/modules" inputs}''${GIO_EXTRA_MODULES:+:$GIO_EXTRA_MODULES}"
        export XDG_DATA_DIRS="${
          lib.concatMapStringsSep ":" (package: "${package}/share/gsettings-schemas/${package.name}") inputs
        }''${XDG_DATA_DIRS:+:$XDG_DATA_DIRS}"
      ''
      + lib.optionalString browser ''
        export WEBUI_BROWSER_PATH="${chromium}/bin/chromium"
        export PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH="${chromium}/bin/chromium"
      ''
      + shellHook;
  }
)
