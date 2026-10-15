# buildRunicApplication: a Runic application built with nixpkgs' buildDotnetModule, without network access in
# the sandbox. See nix/README.md for the arguments and an example flake.
{
  lib,
  stdenvNoCC,
  callPackage,
  buildDotnetModule,
  dotnetCorePackages,
  runCommand,
  makeBinaryWrapper,
  copyDesktopItems,
  writableTmpDirAsHomeHook,
  clang,
  zlib,
  libxml2,
  nodejs_24,
  pnpm,
  bun,
}:
{
  pname,
  version,
  src,
  # The application's project file, relative to src.
  projectFile,
  # packages.lock.json files whose packages restore the project: its own, and those of any project it
  # references or builds. nugetDeps (a deps.json, or a list of NuGet package derivations) adds to them.
  nugetLockFiles ? [ ],
  nugetDeps ? null,
  # Restore in locked mode (RestoreLockedMode), so a lock file that no longer matches the project fails the
  # build instead of being resolved again. The lock files must then have a section for the runtime identifier
  # (<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>), which is checked at evaluation.
  nugetLockedMode ? nugetLockFiles != [ ],
  # The frontend that the Runic MSBuild targets build:
  #   directory       relative to src; default: Frontend beside the project
  #   packageManager  npm, pnpm or bun: the dependencies are installed with it, and the build runs with it
  #   hash            the fixed-output hash of the dependencies; optional for npm, which can fetch each package
  #                   from package-lock.json instead (that reads the lock file at evaluation, so src must be a
  #                   local path such as the flake's own source)
  #   nodeModules     a prepared node_modules derivation instead (null: the frontend has no dependencies)
  #   pnpmFetcherVersion  fetchPnpmDeps' fetcherVersion; default 4
  # Only the frontend directory's package.json, lock file, .npmrc and pnpm-workspace.yaml are fetched from, so a
  # workspace whose members have their own package.json needs nodeModules.
  frontend ? null,
  # desktop, desktop-gtk4, cswebui or null: the libraries the host loads at run time.
  host ? "desktop",
  # GStreamer plugins for audio and video in WebKitGTK.
  withMedia ? false,
  nativeAot ? false,
  selfContainedBuild ? nativeAot,
  # makeDesktopItem results, installed in $out/share/applications.
  desktopItems ? [ ],
  # Icons for the desktop entry: { scalable = ./app.svg; "256x256" = ./app-256.png; }, installed as
  # $out/share/icons/hicolor/<size>/apps/<iconName>.<extension>.
  icons ? { },
  iconName ? pname,
  dotnet-sdk ? dotnetCorePackages.sdk_10_0,
  # Runic's hosts serve the page with ASP.NET Core.
  dotnet-runtime ? dotnet-sdk.aspnetcore,
  nodejs ? nodejs_24,
  runtimeDeps ? [ ],
  nativeBuildInputs ? [ ],
  buildInputs ? [ ],
  dotnetFlags ? [ ],
  dotnetRestoreFlags ? [ ],
  makeWrapperArgs ? [ ],
  passthru ? { },
  meta ? { },
  ...
}@args:
let
  inherit (dotnetCorePackages) fetchNupkg;
  hostRuntime = callPackage ./hosts.nix { } host;
  nugetLock = callPackage ./nuget-lock-deps.nix { inherit fetchNupkg; } {
    lockFiles = checkedLockFiles;
    inherit dotnet-sdk;
  };
  runtimeId = args.runtimeId or (dotnetCorePackages.systemToDotnetRid stdenvNoCC.hostPlatform.system);
  # Locked mode compares the lock with the restore for this runtime identifier, whose section a lock written
  # without RuntimeIdentifiers lacks.
  lockWithoutRuntime = lib.findFirst (
    file: !(lib.any (key: lib.hasSuffix "/${runtimeId}" key) (lib.attrNames ((lib.importJSON file).dependencies or { })))
  ) null (lib.toList nugetLockFiles);
  checkedLockFiles =
    assert lib.assertMsg (!nugetLockedMode || lockWithoutRuntime == null) ''
      buildRunicApplication: ${toString lockWithoutRuntime} has no ${runtimeId} section, so a locked restore for ${runtimeId} fails.
      Set <RuntimeIdentifiers>${runtimeId}</RuntimeIdentifiers> in the project and run dotnet restore again, or set nugetLockedMode = false.'';
    nugetLockFiles;
  extraNugetDeps =
    if nugetDeps == null then
      [ ]
    else if lib.isDerivation nugetDeps then
      [ nugetDeps ]
    else if lib.isList nugetDeps then
      nugetDeps
    else
      map fetchNupkg (lib.importJSON nugetDeps);

  frontend' =
    if frontend == null then
      null
    else
      let
        directory = frontend.directory or "${dirOf projectFile}/Frontend";
        packageManager =
          frontend.packageManager or (throw "buildRunicApplication: set frontend.packageManager to npm, pnpm or bun");
        # Only what the install reads, so an edit to the frontend's sources does not fetch again.
        lockSource =
          runCommand "${pname}-frontend-lock" { } ''
            mkdir $out
            for file in package.json package-lock.json npm-shrinkwrap.json pnpm-lock.yaml pnpm-workspace.yaml bun.lock .npmrc; do
              if [[ -e ${src}/${directory}/$file ]]; then cp ${src}/${directory}/$file $out/; fi
            done
          '';
        nodeModules =
          if frontend ? nodeModules then
            frontend.nodeModules
          else
            callPackage ./frontend-node-modules.nix { inherit nodejs pnpm bun; } {
              inherit pname version packageManager;
              src = if packageManager == "npm" && frontend.hash or null == null then "${src}/${directory}" else lockSource;
              hash = frontend.hash or null;
              pnpmFetcherVersion = frontend.pnpmFetcherVersion or 4;
            };
      in
      {
        inherit directory packageManager nodeModules;
        tool =
          {
            npm = [ ];
            pnpm = [ pnpm ];
            bun = [ bun ];
          }
          .${packageManager};
      };

in
buildDotnetModule (
  removeAttrs args [
    "nugetLockFiles"
    "nugetLockedMode"
    "frontend"
    "host"
    "withMedia"
    "nativeAot"
    "desktopItems"
    "icons"
    "iconName"
    "nodejs"
  ]
  // {
    inherit
      pname
      version
      src
      projectFile
      dotnet-sdk
      dotnet-runtime
      selfContainedBuild
      desktopItems
      ;
    nugetDeps = nugetLock.packages ++ extraNugetDeps;

    # nixpkgs installs NuGet packages into the fallback folder with an empty contentHash, which a locked restore
    # rejects (NU1403). Restore each locked package from the global packages folder instead, linked to the same
    # files, with the hash its lock records: the download was checked against it, and a package that the .NET
    # SDK provides is the same version.
    postConfigureNuGet = lib.optionalString (nugetLockFiles != [ ]) ''
      while read -r id packageVersion contentHash; do
        source="''${NUGET_FALLBACK_PACKAGES%/}/$id/$packageVersion"
        [[ -d $source ]] || continue
        target="''${NUGET_PACKAGES%/}/$id/$packageVersion"
        mkdir -p "$target"
        for file in "$source"/* "$source"/.[!.]*; do
          if [[ -e $file && ''${file##*/} != .nupkg.metadata ]]; then ln -s "$file" "$target/"; fi
        done
        printf '{"version":2,"contentHash":"%s","source":null}\n' "$contentHash" > "$target/.nupkg.metadata"
      done <<'RUNIC_LOCKED_PACKAGES'
    ${lib.concatStringsSep "\n" nugetLock.lockedPackages}
    RUNIC_LOCKED_PACKAGES
      ${args.postConfigureNuGet or ""}
    '';

    nativeBuildInputs =
      nativeBuildInputs
      ++ [
        makeBinaryWrapper
        copyDesktopItems
        writableTmpDirAsHomeHook
        libxml2
        nodejs
      ]
      ++ lib.optional (hostRuntime.wrapHook != null) hostRuntime.wrapHook
      ++ lib.optionals (frontend' != null) frontend'.tool
      ++ lib.optional nativeAot clang;
    buildInputs =
      buildInputs
      ++ hostRuntime.buildInputs
      ++ lib.optionals withMedia hostRuntime.gstreamer
      ++ lib.optional nativeAot zlib;
    runtimeDeps = runtimeDeps ++ hostRuntime.libraries;

    # NU1004 names the changed references; the lock is updated with dotnet restore outside the sandbox.
    dotnetRestoreFlags = dotnetRestoreFlags ++ lib.optional nugetLockedMode "-p:RestoreLockedMode=true";

    dotnetFlags =
      dotnetFlags
      ++ lib.optional nativeAot "-p:PublishAot=true"
      ++ lib.optionals (frontend' != null && frontend'.nodeModules != null) [
        "-p:RunicApplicationFrontendNodeModulesDirectory=${frontend'.nodeModules}/node_modules"
        # Runic.Application.Views before 0.8.0-preview.1 knows only this property; the link below serves it.
        "-p:RunicApplicationFrontendInstallEnabled=false"
      ];

    # The installed application's prefix, for build-time paths such as a privileged action's Program:
    #   Program="$(RunicApplicationInstallPrefix)/bin/my-app"
    preConfigure = ''
      export RunicApplicationInstallPrefix=$out
      # The frontend build runs offline with read-only node_modules: pnpm must neither fetch the version that
      # package.json names nor install again before `pnpm run`.
      export pnpm_config_pm_on_fail=ignore pnpm_config_verify_deps_before_run=false npm_config_update_notifier=false
      ${lib.optionalString (frontend' != null && frontend'.nodeModules != null) ''
        if [[ ! -e ${frontend'.directory}/node_modules ]]; then
          ln -s ${frontend'.nodeModules}/node_modules ${frontend'.directory}/node_modules
        fi
      ''}
      ${args.preConfigure or ""}
    '';

    inherit makeWrapperArgs;

  }
  // lib.optionalAttrs (nativeAot && !(args ? installPhase)) {
    # buildDotnetModule publishes with --no-build. Runic.Application.Views 0.7.0-preview.9 and earlier then build
    # the bridge's bootstrap pass again for NativeAOT, which --no-build refuses (NETSDK1085), so publish lets
    # MSBuild skip what the build phase left up to date instead.
    installPhase = ''
      runHook preInstall
      local -a projectFiles flags installFlags runtimeIds
      concatTo projectFiles dotnetProjectFiles
      concatTo flags dotnetFlags
      concatTo installFlags dotnetInstallFlags
      concatTo runtimeIds dotnetRuntimeIds
      for projectFile in "''${projectFiles[@]}"; do
        for runtimeId in "''${runtimeIds[@]}"; do
          dotnet publish "$projectFile" -maxcpucount:"$NIX_BUILD_CORES" -p:ContinuousIntegrationBuild=true \
            -p:Deterministic=true -p:OverwriteReadOnlyFiles=true --output "''${dotnetInstallPath-$out/lib/$pname}" \
            --configuration "''${dotnetBuildType-Release}" --no-restore --runtime "$runtimeId" --self-contained \
            "''${flags[@]}" "''${installFlags[@]}"
        done
      done
      runHook postInstall
    '';
  }
  // {
    postInstall = ''
      # The wrappers are binaries: a privileged action may name one as its program, and pkexec then runs no shell.
      # A binary wrapper cannot --run a command (useDotnetFromEnv does), so that one is a shell script instead.
      # Login startup registers the wrapper ($2), not the unwrapped program behind it.
      makeWrapper() {
        local argument
        for argument in "$@"; do
          if [[ $argument == --run ]]; then
            makeShellWrapper "$@" --set RUNIC_APPLICATION_EXECUTABLE "$2"
            return
          fi
        done
        makeBinaryWrapper "$@" --set RUNIC_APPLICATION_EXECUTABLE "$2"
      }

      appDir=''${dotnetInstallPath-$out/lib/$pname}
      rm -f "$appDir"/*.dbg

      # Runic.Platform.Linux.Polkit writes the policy to share/polkit-1/actions in the publish directory.
      for policy in "$appDir"/share/polkit-1/actions/*.policy; do
        [[ -e $policy ]] || continue
        install -Dm644 "$policy" $out/share/polkit-1/actions/"''${policy##*/}"
        rm "$policy"
      done
      rmdir -p --ignore-fail-on-non-empty "$appDir/share/polkit-1/actions" 2>/dev/null || true

      ${lib.concatStrings (
        lib.mapAttrsToList (size: icon: ''
          install -Dm644 ${icon} $out/share/icons/hicolor/${size}/apps/${iconName}${
            if lib.hasSuffix ".svg" (toString icon) then ".svg" else ".png"
          }
        '') icons
      )}
      ${args.postInstall or ""}
    '';

    postFixup = ''
      # pkexec runs a privileged action's program as root, from the policy's exec.path: it must be a program of
      # this package, a regular file at its real path.
      for policy in $out/share/polkit-1/actions/*.policy; do
        [[ -e $policy ]] || continue
        while IFS= read -r program; do
          [[ -n $program ]] || continue
          if [[ $program != "$out"/* ]]; then
            echo "error: $policy runs $program, which is not in $out. Set the privileged action's Program to \$(RunicApplicationInstallPrefix)/bin/<executable>." >&2
            exit 1
          fi
          if [[ ! -f $program || -L $program || ! -x $program || $(realpath "$program") != "$program" ]]; then
            echo "error: $policy runs $program, which is not an executable regular file at its real path." >&2
            exit 1
          fi
        done < <(xmllint --xpath '//annotate[@key="org.freedesktop.policykit.exec.path"]/text()' "$policy" 2>/dev/null; echo)
        xmllint --noout "$policy"
      done
      ${args.postFixup or ""}
    '';

    passthru =
      passthru
      // lib.optionalAttrs (frontend' != null) { frontendNodeModules = frontend'.nodeModules; }
      // {
        inherit host nativeAot;
      };

    meta = {
      platforms = lib.platforms.linux;
    }
    // meta;
  }
)
