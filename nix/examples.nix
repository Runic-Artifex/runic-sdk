# SDK examples packaged with buildRunicApplication from this checkout. They reference the SDK's projects, so
# their source is the SDK tree and their NuGet packages come from its lock files.
{ pkgs, runic }:
let
  inherit (pkgs) lib;
  root = ../.;
  sdkSource =
    extra:
    lib.fileset.toSource {
      inherit root;
      fileset = lib.fileset.unions (
        [
          ../Directory.Build.props
          ../Directory.Build.targets
          ../Directory.Packages.props
          ../global.json
          ../NuGet.config
          ../eng/Versions.props
          ../eng/build
          ../packages/dotnet
          ../tools/Runic.Application.Views.Codegen
          ../tools/Runic.Application.Views.Codegen.ReactiveUI
          ../tools/Runic.Application.Views.Codegen.Toolkit
          ../examples/Directory.Build.props
          ../examples/Directory.Build.targets
          ../examples/Directory.Packages.props
          ../examples/NuGet.config
        ]
        ++ extra
      );
    };
  sdkLockFiles =
    lib.filter lib.pathExists (
      map (name: ../packages/dotnet/${name}/packages.lock.json) (lib.attrNames (builtins.readDir ../packages/dotnet))
    )
    ++ [
      ../tools/Runic.Application.Views.Codegen/packages.lock.json
      ../tools/Runic.Application.Views.Codegen.ReactiveUI/packages.lock.json
      ../tools/Runic.Application.Views.Codegen.Toolkit/packages.lock.json
    ];

  # The SDK's projects also target net10.0-windows, whose targeting pack the .NET SDK does not include.
  windowsTargetingPack = [
    (pkgs.dotnetCorePackages.fetchNupkg {
      pname = "Microsoft.WindowsDesktop.App.Ref";
      version = "10.0.12";
      hash = "sha256-omruH9MPqONFT43CGi3/ZDuw8UkqIp0kZhTfq/CMr4k=";
    })
  ];

  # The examples' pages import @runic-artifex/views from the SDK's Bun workspace; here it is built from source.
  views = pkgs.stdenvNoCC.mkDerivation {
    pname = "runic-artifex-views";
    version = (lib.importJSON ../packages/web/views/package.json).version;
    src = lib.fileset.toSource {
      root = ../packages/web/views;
      fileset = lib.fileset.unions [
        ../packages/web/views/package.json
        ../packages/web/views/tsconfig.json
        ../packages/web/views/tsconfig.build.json
        ../packages/web/views/src
      ];
    };
    nativeBuildInputs = [ pkgs.typescript ];
    buildPhase = ''
      runHook preBuild
      tsc -p tsconfig.build.json
      runHook postBuild
    '';
    installPhase = ''
      runHook preInstall
      mkdir -p $out/node_modules/@runic-artifex/views
      cp -r package.json dist $out/node_modules/@runic-artifex/views/
      runHook postInstall
    '';
  };

  statusIcon =
    { nativeAot }:
    runic.buildRunicApplication pkgs (
      {
        pname = "runic-status-icon${lib.optionalString nativeAot "-aot"}";
        version = "0.0.0";
        src = sdkSource [ ../examples/status-icon ];
        projectFile = "examples/status-icon/StatusIconSample.csproj";
        nugetLockFiles = sdkLockFiles ++ [ ../examples/status-icon/packages.lock.json ];
        nugetDeps = windowsTargetingPack;
        # The SDK's lock files are restored without a runtime identifier; CI restores them in locked mode.
        nugetLockedMode = false;
        # Package validation would download each project's last published package.
        dotnetFlags = [ "-p:EnablePackageValidation=false" ];
        frontend = {
          packageManager = "bun";
          nodeModules = views;
        };
        inherit nativeAot;
        executables = [ "StatusIconSample" ];
        desktopItems = [
          (pkgs.makeDesktopItem {
            name = "dev.runic.examples.StatusIcon";
            desktopName = "Status icon sample";
            exec = "StatusIconSample";
            icon = "dev.runic.examples.StatusIcon";
            categories = [ "Utility" ];
          })
        ];
        icons."512x512" = ../eng/branding/desktop/icon.png;
        iconName = "dev.runic.examples.StatusIcon";
        meta.mainProgram = "StatusIconSample";
      }
    );

  # Both sides of a privileged action, from tests/native: its policy names the installed program.
  privilegedSmoke = runic.buildRunicApplication pkgs {
    pname = "runic-privileged-smoke";
    version = "0.0.0";
    src = sdkSource [ ../tests/native/Runic.Privileged.Smoke ];
    projectFile = "tests/native/Runic.Privileged.Smoke/Runic.Privileged.Smoke.csproj";
    nugetLockFiles = sdkLockFiles ++ [ ../tests/native/Runic.Privileged.Smoke/packages.lock.json ];
    nugetDeps = windowsTargetingPack;
    nugetLockedMode = false;
    dotnetFlags = [ "-p:EnablePackageValidation=false" ];
    host = null;
    nativeAot = true;
    executables = [ "Runic.Privileged.Smoke" ];
    # The smoke's Program property; an application writes $(RunicApplicationInstallPrefix)/bin/<name> itself.
    preConfigure = ''
      export RunicPrivilegedSmokeProgram=$out/bin/Runic.Privileged.Smoke
    '';
    meta.mainProgram = "Runic.Privileged.Smoke";
  };
in
{
  status-icon = statusIcon { nativeAot = false; };
  status-icon-aot = statusIcon { nativeAot = true; };
  privileged-smoke = privilegedSmoke;
}
