# buildRunicApplication: a Runic application built with nixpkgs' buildDotnetModule, without network access in
# the sandbox. See nix/README.md for the arguments and an example flake.
{
  lib,
  stdenvNoCC,
  callPackage,
  buildDotnetModule,
  dotnetCorePackages,
  runCommand,
  writeText,
  makeBinaryWrapper,
  copyDesktopItems,
  writableTmpDirAsHomeHook,
  clang,
  zlib,
  openssl,
  libxml2,
  buildPackages,
  installShellFiles,
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
  # references or builds. nugetDeps (a deps.json, or a list of NuGet package derivations) adds to them. For
  # several runtimes, { linux-x64 = [ ... ]; linux-arm64 = [ ... ]; }: a locked restore needs a lock file per
  # runtime identifier.
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
  #                   from package-lock.json instead. That reads the lock file at evaluation, from src when it is
  #                   a path, from its unfiltered source when lib.fileset.toSource, lib.cleanSource or
  #                   lib.cleanSourceWith filters it, and otherwise from src's store path, which then has to exist.
  #   nodeModules     a prepared node_modules derivation instead (null: the frontend has no dependencies)
  #   pnpmFetcherVersion  fetchPnpmDeps' fetcherVersion; default 4
  # Only the frontend directory's package.json, lock file, .npmrc and pnpm-workspace.yaml are fetched from, so a
  # workspace whose members have their own package.json needs nodeModules.
  frontend ? null,
  # Local .NET tools that the build runs with `dotnet tool run`, such as dotnet-runic-translations for the
  # Translations Vite plugin, restored offline from their manifest:
  #   manifest  the dotnet-tools.json, relative to src; default: .config/dotnet-tools.json beside the project. It is
  #             read at evaluation from src when it is a path, from its unfiltered source when it is filtered by
  #             lib.fileset.toSource, lib.cleanSource or lib.cleanSourceWith, and otherwise from src's store path,
  #             which then has to exist. A path (./App/.config/dotnet-tools.json) is read and restored from as
  #             it is, for any src, such as one that builtins.path filters.
  #   hashes    { <package ID> = <hash of the .nupkg>; } for each tool in it
  dotnetTools ? null,
  # Shell completions that the program prints, installed for bash, zsh and fish (share/bash-completion,
  # share/zsh/site-functions, share/fish/vendor_completions.d) by running the installed program after the build:
  #   program  the program in bin; default meta.mainProgram
  #   command  its arguments before the shell's name; default [ "completion" ], Runic.CommandLine's command.
  #            [ "--completions" ] for an option declared with CommandCatalogBuilder.CompletionOption.
  #   shells   default [ "bash" "zsh" "fish" ]
  # A cross build cannot run the program and installs none.
  completions ? null,
  # desktop, desktop-gtk4, cswebui or null: the libraries the host loads at run time.
  host ? "desktop",
  # GStreamer plugins for audio and video in WebKitGTK.
  withMedia ? false,
  # WebKitGTK with speech synthesis, which links flite. false uses a WebKitGTK built without it, about 70 MiB smaller,
  # which nixpkgs' binary cache does not have: WebKitGTK then builds from source, for hours.
  withSpeechSynthesis ? true,
  # Hunspell dictionaries for WebKitGTK's spell checker, such as [ hunspellDicts.de_CH hunspellDicts.en_US ]: the
  # wrapper puts their share directories on XDG_DATA_DIRS, where enchant finds share/hunspell. A desktop may have none,
  # and then nothing is checked. Only the desktop and desktop-gtk4 hosts check spelling.
  spellingDictionaries ? [ ],
  # Publish projectFile with NativeAOT. Only that project gets PublishAot, as if its project file set it: the projects
  # it references and testProjectFile are restored and built as their own project files say.
  nativeAot ? false,
  selfContainedBuild ? nativeAot,
  # How doCheck runs testProjectFile, named as in global.json's test.runner:
  #   VSTest                      nixpkgs' `dotnet test`, with testFilters and disabledTests
  #   Microsoft.Testing.Platform  each test project's built test application (TUnit; MSTest or xUnit.net v3 with
  #                               their Microsoft.Testing.Platform runner), with dotnetTestFlags as its options
  testRunner ? "VSTest",
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
  hostRuntime = callPackage ./hosts.nix (lib.optionalAttrs (!withSpeechSynthesis) (
    lib.getAttrs [ "webkitgtk_4_1" "webkitgtk_6_0" ] (callPackage ./webkitgtk-without-speech-synthesis.nix { })
  )) host;
  nugetLock = callPackage ./nuget-lock-deps.nix { inherit fetchNupkg; } {
    lockFiles = checkedLockFiles;
    inherit dotnet-sdk;
  };
  runtimeId = args.runtimeId or (dotnetCorePackages.systemToDotnetRid stdenvNoCC.hostPlatform.system);
  lockFiles =
    if lib.isAttrs nugetLockFiles && !lib.isDerivation nugetLockFiles then
      lib.toList (
        nugetLockFiles.${runtimeId} or (throw ''
          buildRunicApplication: nugetLockFiles has no lock files for ${runtimeId}, only for ${lib.concatStringsSep ", " (lib.attrNames nugetLockFiles)}.
          Restore the projects for it (dotnet restore -p:RuntimeIdentifiers=${runtimeId}) into their own lock files and add them as nugetLockFiles.${runtimeId}; see nix/README.md.'')
      )
    else
      lib.toList nugetLockFiles;
  # Locked mode compares each lock with a restore for this runtime identifier alone (dotnet restore --runtime): a
  # lock written without RuntimeIdentifiers lacks its section, and one written for several runtimes has others.
  lockRuntimes =
    file:
    lib.unique (
      map (key: lib.last (lib.splitString "/" key)) (
        lib.filter (lib.hasInfix "/") (lib.attrNames ((lib.importJSON file).dependencies or { }))
      )
    );
  lockWithoutRuntime = lib.findFirst (file: !(lib.elem runtimeId (lockRuntimes file))) null lockFiles;
  lockWithOtherRuntimes = lib.findFirst (file: lockRuntimes file != [ runtimeId ]) null lockFiles;
  checkedLockFiles =
    assert lib.assertMsg (!nugetLockedMode || lockWithoutRuntime == null) ''
      buildRunicApplication: ${toString lockWithoutRuntime} has no ${runtimeId} section, so a locked restore for ${runtimeId} fails.
      Set <RuntimeIdentifiers>${runtimeId}</RuntimeIdentifiers> in the project and run dotnet restore again, or set nugetLockedMode = false.'';
    assert lib.assertMsg (!nugetLockedMode || lockWithOtherRuntimes == null) ''
      buildRunicApplication: ${toString lockWithOtherRuntimes} has sections for ${lib.concatStringsSep ", " (lockRuntimes lockWithOtherRuntimes)}, but a locked restore for ${runtimeId} restores that runtime alone and fails (NU1004).
      Keep one lock file per runtime identifier, and pass them as nugetLockFiles = { ${runtimeId} = [ ... ]; ... }; see nix/README.md.'';
    lockFiles;
  extraNugetDeps =
    if nugetDeps == null then
      [ ]
    else if lib.isDerivation nugetDeps then
      [ nugetDeps ]
    else if lib.isList nugetDeps then
      nugetDeps
    else
      map fetchNupkg (lib.importJSON nugetDeps);

  # A file of src that is read at evaluation, by its path relative to src. The store path of a filtered source, or
  # of a path's copy ("${./.}"), exists only once a writable evaluation has added it, so reading from it fails a
  # read-only one (nix flake check --no-build) on an empty store. The path itself, or the filtered source's
  # unfiltered one, is read instead.
  sourceForEvaluation =
    relative:
    if src._isLibCleanSourceWith or false then
      src.origSrc + "/${relative}"
    else if builtins.isPath src then
      src + "/${relative}"
    else
      "${src}/${relative}";

  # Each tool package is a fixed-output download from nuget.org, linked into the restore's packages folder
  # (postConfigureNuGet), where `dotnet tool restore` finds it without a source.
  dotnetTools' =
    if dotnetTools == null then
      null
    else
      let
        manifest = dotnetTools.manifest or "${dirOf (lib.head (lib.toList projectFile))}/.config/dotnet-tools.json";
        manifestFile = if builtins.isPath manifest then manifest else sourceForEvaluation manifest;
        tools = (lib.importJSON manifestFile).tools or { };
        hashes = dotnetTools.hashes or { };
        # All of them at once, rather than one per evaluation.
        missing = lib.filter (id: !hashes ? ${id}) (lib.attrNames tools);
        fetch =
          id: tool:
          fetchNupkg {
            pname = id;
            inherit (tool) version;
            hash = hashes.${id};
          };
      in
      {
        # Relative to src, or the manifest's own store path.
        manifest = if builtins.isPath manifest then "${manifest}" else manifest;
        packages =
          assert lib.assertMsg (missing == [ ]) ''
            buildRunicApplication: dotnetTools.hashes has no hash for ${
              lib.concatMapStringsSep ", " (id: "${id} ${tools.${id}.version}") missing
            }, tools in ${toString manifest}.
            Run `nix run .#<package>.fixedOutputHashes`, which prints every hash, or set each to lib.fakeHash, build with --keep-going, and copy the hashes Nix reports.'';
          lib.mapAttrsToList fetch tools;
        # Each tool's download under lib.fakeHash, for passthru.fixedOutputHashes.
        fakeHashDownloads = lib.mapAttrs (
          id: tool:
          (fetchNupkg {
            pname = id;
            inherit (tool) version;
            hash = lib.fakeHash;
          }).src
        ) tools;
      };

  frontend' =
    if frontend == null then
      null
    else
      let
        directory = frontend.directory or "${dirOf projectFile}/Frontend";
        packageManager =
          frontend.packageManager or (throw "buildRunicApplication: set frontend.packageManager to npm, pnpm or bun");
        # Only what the install reads, so an edit to the frontend's sources does not fetch again.
        lockFileNames = [
          "package.json"
          "package-lock.json"
          "npm-shrinkwrap.json"
          "pnpm-lock.yaml"
          "pnpm-workspace.yaml"
          "bun.lock"
          ".npmrc"
        ];
        lockSource =
          runCommand "${pname}-frontend-lock" { } ''
            mkdir $out
            for file in ${lib.escapeShellArgs lockFileNames}; do
              if [[ -e ${src}/${directory}/$file ]]; then cp ${src}/${directory}/$file $out/; fi
            done
          '';
        # A digest of the lock files in the names of the fixed-output downloads, so a changed lock file under the
        # previous hash is fetched again and its hash reported. Read at evaluation only where that reads no store
        # path (a path, or a filtered one): a source that is a derivation keeps names without it.
        lockDigest =
          if builtins.isPath src || src._isLibCleanSourceWith or false then
            builtins.substring 0 12 (
              builtins.hashString "sha256" (
                lib.concatMapStrings (
                  file:
                  let
                    path = sourceForEvaluation "${directory}/${file}";
                  in
                  if builtins.pathExists path then "${file}\n${builtins.hashFile "sha256" path}\n" else ""
                ) lockFileNames
              )
            )
          else
            null;
        nodeModulesWith =
          hash:
          callPackage ./frontend-node-modules.nix { inherit nodejs pnpm bun; } {
            inherit
              pname
              version
              packageManager
              hash
              lockDigest
              ;
            # importNpmLock reads package.json and package-lock.json at evaluation.
            src =
              if packageManager == "npm" && frontend.hash or null == null then
                sourceForEvaluation directory
              else
                lockSource;
            pnpmFetcherVersion = frontend.pnpmFetcherVersion or 4;
          };
        nodeModules = if frontend ? nodeModules then frontend.nodeModules else nodeModulesWith (frontend.hash or null);
        hashPerSystem = lib.isAttrs (frontend.hash or null);
      in
      {
        inherit
          directory
          packageManager
          nodeModules
          hashPerSystem
          ;
        # The downloads under lib.fakeHash, for each system that frontend.hash names, for passthru.fixedOutputHashes.
        fakeHashDownloads =
          if frontend ? nodeModules || (packageManager == "npm" && frontend.hash or null == null) then
            { }
          else
            (nodeModulesWith (
              if hashPerSystem then lib.mapAttrs (_: _: lib.fakeHash) frontend.hash else lib.fakeHash
            )).fixedOutputs;
        tool =
          {
            npm = [ ];
            pnpm = [ pnpm ];
            bun = [ bun ];
          }
          .${packageManager};
      };

  # dotnet test speaks VSTest unless global.json selects Microsoft.Testing.Platform in the directory it runs from,
  # and its VSTest options (--logger, --filter) are not test application options. The check runs each test
  # application itself instead, from the path the build wrote it to, with the build's properties.
  testRunner' =
    assert lib.assertMsg (lib.elem testRunner [
      "VSTest"
      "Microsoft.Testing.Platform"
    ]) "buildRunicApplication: testRunner must be VSTest or Microsoft.Testing.Platform, not ${toString testRunner}";
    assert lib.assertMsg (testRunner == "VSTest" || !(args.doCheck or false) || args.testProjectFile or null != null)
      "buildRunicApplication: testRunner = \"Microsoft.Testing.Platform\" runs testProjectFile's test application; set testProjectFile.";
    assert lib.assertMsg (testRunner == "VSTest" || (args.testFilters or [ ] == [ ] && args.disabledTests or [ ] == [ ]))
      "buildRunicApplication: testFilters and disabledTests are VSTest filters. With Microsoft.Testing.Platform, pass the test framework's own filter option in dotnetTestFlags, such as [ \"--treenode-filter\" \"/*/*/*/*[Category!=Slow]\" ] for TUnit.";
    testRunner;
  testingPlatformCheckPhase = ''
    runHook preCheck
    local -a testProjectFiles flags testFlags runtimeIds runtimeDeps
    concatTo testProjectFiles dotnetTestProjectFiles
    concatTo flags dotnetFlags
    concatTo testFlags dotnetTestFlags
    concatTo runtimeIds dotnetRuntimeIds
    concatTo runtimeDeps dotnetRuntimeDeps
    if [[ -v dotnetSelfContainedBuild ]]; then
      if [[ -n $dotnetSelfContainedBuild ]]; then flags+=("-p:SelfContained=true"); else flags+=("-p:SelfContained=false"); fi
    fi
    local libraryPath=''${LD_LIBRARY_PATH-}
    if (( ''${#runtimeDeps[@]} > 0 )); then
      local -a libraryPaths=("''${runtimeDeps[@]/%//lib}")
      local IFS=:
      libraryPath="''${libraryPaths[*]}''${libraryPath:+:}$libraryPath"
      unset IFS
    fi
    local projectFile runtimeId testApplication
    for projectFile in "''${testProjectFiles[@]}"; do
      for runtimeId in "''${runtimeIds[@]}"; do
        testApplication=$(dotnet msbuild "$projectFile" -nologo -getProperty:TargetPath \
          -p:Configuration="''${dotnetBuildType-Release}" -p:RuntimeIdentifier="$runtimeId" "''${flags[@]}")
        echo "Running $testApplication"
        LD_LIBRARY_PATH=$libraryPath dotnet "$testApplication" "''${testFlags[@]}"
      done
    done
    runHook postCheck
  '';

  completions' =
    if completions == null then
      null
    else
      let
        program =
          completions.program or args.meta.mainProgram or (throw
            "buildRunicApplication: completions needs the program that prints them: set completions.program or meta.mainProgram.");
        shells = completions.shells or [
          "bash"
          "zsh"
          "fish"
        ];
      in
      assert lib.assertMsg (lib.all (shell: lib.elem shell [ "bash" "zsh" "fish" ]) shells)
        "buildRunicApplication: completions.shells may name bash, zsh and fish, not ${lib.concatStringsSep ", " shells}.";
      {
        inherit program shells;
        command = completions.command or [ "completion" ];
      };

  # nativeAot sets PublishAot in the evaluation of projectFile alone, through CustomAfterDirectoryBuildProps, rather
  # than with -p:PublishAot=true, a global property that also reaches testProjectFile and its own restore, which then
  # needs a NativeAOT lock file (#316). Projects whose path the build exports as RunicNativeAotProjectFiles
  # (preConfigure) get it; the application's project then sees PublishAot in its own body, as a lock file path
  # chosen there with Condition="'$(PublishAot)' == 'true'" needs.
  nativeAotProps = writeText "runic-native-aot.props" ''
    <Project>
      <PropertyGroup Condition="$([System.String]::Copy(';$(RunicNativeAotProjectFiles);').Contains(';$(MSBuildProjectFullPath);'))">
        <PublishAot>true</PublishAot>
      </PropertyGroup>
    </Project>
  '';

in
buildDotnetModule (
  removeAttrs args [
    "nugetLockFiles"
    "nugetLockedMode"
    "frontend"
    "host"
    "withMedia"
    "withSpeechSynthesis"
    "spellingDictionaries"
    "nativeAot"
    "desktopItems"
    "icons"
    "iconName"
    "nodejs"
    "testRunner"
    "completions"
    "dotnetTools"
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
    postConfigureNuGet =
      lib.optionalString (dotnetTools' != null) ''
        for package in ${lib.escapeShellArgs dotnetTools'.packages}; do
          for directory in "$package"/share/nuget/packages/*/*; do
            target="''${NUGET_PACKAGES%/}/''${directory#"$package"/share/nuget/packages/}"
            mkdir -p "''${target%/*}"
            ln -sfn "$directory" "$target"
          done
        done
      ''
      + lib.optionalString (nugetLockFiles != [ ]) ''
      while read -r id packageVersion contentHash; do
        source="''${NUGET_FALLBACK_PACKAGES%/}/$id/$packageVersion"
        [[ -d $source ]] || continue
        target="''${NUGET_PACKAGES%/}/$id/$packageVersion"
        # With a tool manifest at the root of src, nixpkgs links each package's directory here already.
        if [[ -L $target ]]; then rm "$target"; fi
        mkdir -p "$target"
        for file in "$source"/* "$source"/.[!.]*; do
          if [[ -e $file && ''${file##*/} != .nupkg.metadata ]]; then ln -s "$file" "$target/"; fi
        done
        printf '{"version":2,"contentHash":"%s","source":null}\n' "$contentHash" > "$target/.nupkg.metadata"
      done <<'RUNIC_LOCKED_PACKAGES'
    ${lib.concatStringsSep "\n" nugetLock.lockedPackages}
    RUNIC_LOCKED_PACKAGES
    ''
      + args.postConfigureNuGet or "";

  }
  // lib.optionalAttrs (dotnetTools' != null) {
    # A tool manifest at the root of src makes nixpkgs link every NuGet package into the packages folder for its
    # own `dotnet tool restore`, which fails on the packages that the .NET SDK provides as well ("File exists").
    # The tools are linked there above instead.
    linkNugetPackages = "";
  }
  // {
    nativeBuildInputs =
      nativeBuildInputs
      ++ [
        makeBinaryWrapper
        copyDesktopItems
        installShellFiles
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
    # A self-contained or NativeAOT application carries the runtime packs, whose crypto shim dlopen()s libssl.
    # nixpkgs' source-built packs name it by store path; the official binary packs (e.g. with
    # dotnetCorePackages.sdk_10_0-bin) name only libssl.so.3. Their SDK links an rpath for it, but the fixup's
    # patchelf --shrink-rpath drops it as the library is not DT_NEEDED, so provide it on LD_LIBRARY_PATH as
    # buildDotnetModule does for ICU. A framework-dependent application uses dotnet-runtime, which finds libssl.
    runtimeDeps = runtimeDeps ++ hostRuntime.libraries ++ lib.optional selfContainedBuild openssl;

    # NU1004 names the changed references; the lock is updated with dotnet restore outside the sandbox.
    dotnetRestoreFlags = dotnetRestoreFlags ++ lib.optional nugetLockedMode "-p:RestoreLockedMode=true";

    dotnetFlags =
      dotnetFlags
      ++ lib.optional nativeAot "-p:CustomAfterDirectoryBuildProps=${nativeAotProps}"
      ++ lib.optionals (frontend' != null && frontend'.nodeModules != null) [
        "-p:RunicApplicationFrontendNodeModulesDirectory=${frontend'.nodeModules}/node_modules"
        # Runic.Application.Views before 0.8.0-preview.1 knows only this property; the link below serves it.
        "-p:RunicApplicationFrontendInstallEnabled=false"
      ];

    # The installed application's prefix, for build-time paths such as a privileged action's Program:
    #   Program="$(RunicApplicationInstallPrefix)/bin/my-app"
    preConfigure = ''
      export RunicApplicationInstallPrefix=$out
      ${lib.optionalString nativeAot ''
        RunicNativeAotProjectFiles=
        for runicAotProject in ${lib.escapeShellArgs (lib.toList projectFile)}; do
          RunicNativeAotProjectFiles+="$(realpath --canonicalize-missing --no-symlinks "$runicAotProject");"
        done
        export RunicNativeAotProjectFiles=''${RunicNativeAotProjectFiles%;}
      ''}
      ${lib.optionalString (dotnetTools' != null) ''
        dotnet tool restore --tool-manifest ${lib.escapeShellArg dotnetTools'.manifest}
      ''}
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

    makeWrapperArgs =
      assert lib.assertMsg (spellingDictionaries == [ ] || lib.elem host [ "desktop" "desktop-gtk4" ])
        "buildRunicApplication: spellingDictionaries needs host = \"desktop\" or \"desktop-gtk4\", not ${if host == null then "null" else host}.";
      makeWrapperArgs
      ++ lib.optionals (spellingDictionaries != [ ]) [
        "--prefix"
        "XDG_DATA_DIRS"
        ":"
        (lib.makeSearchPath "share" spellingDictionaries)
      ];

  }
  // lib.optionalAttrs (testRunner' == "Microsoft.Testing.Platform" && !(args ? checkPhase)) {
    checkPhase = testingPlatformCheckPhase;
  }
  // lib.optionalAttrs (nativeAot && !(args ? installPhase)) {
    # buildDotnetModule publishes with --no-build. Runic.Application.Views 0.8.0-preview.1 and earlier then build
    # the bridge's bootstrap pass again for NativeAOT, which --no-build refuses (NETSDK1085), so publish lets
    # MSBuild skip what the build phase left up to date instead. Later versions reuse the generated code under
    # --no-build (#278); keep this while the builder supports applications on the earlier versions.
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
      # The file types and URL schemes that the project declares (RunicApplicationFileTypes,
      # RunicApplicationUrlSchemes), installed as dotnet runic package installs them in a deb or rpm: the shared-mime-info
      # definitions, and MimeType= and %U in the desktop entry named after ApplicationId.
      local -a runicProjectFiles runicFlags
      concatTo runicProjectFiles dotnetProjectFiles
      concatTo runicFlags dotnetFlags
      local projectFile
      for projectFile in "''${runicProjectFiles[@]}"; do
        dotnet msbuild "$projectFile" -nologo -p:Configuration="''${dotnetBuildType-Release}" "''${runicFlags[@]}" \
          -getProperty:ApplicationId,ApplicationTitle,AssemblyName,RunicApplicationFileTypes,RunicApplicationUrlSchemes \
          > "$TMPDIR/runic-application-properties.json"
        ${buildPackages.python3.interpreter} -I ${./desktop-integration.py} "$TMPDIR/runic-application-properties.json" "$projectFile" "$out"
      done

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
      ${lib.optionalString (completions' != null && stdenvNoCC.buildPlatform.canExecute stdenvNoCC.hostPlatform) ''
        # The program prints each shell's script through its wrapper, with a home of its own.
        runicCompletions=$(mktemp -d)
        for shell in ${lib.escapeShellArgs completions'.shells}; do
          HOME=$runicCompletions/home XDG_CONFIG_HOME=$runicCompletions/config XDG_DATA_HOME=$runicCompletions/data \
            XDG_STATE_HOME=$runicCompletions/state XDG_CACHE_HOME=$runicCompletions/cache \
            $out/bin/${lib.escapeShellArg completions'.program} ${lib.escapeShellArgs completions'.command} "$shell" \
            > "$runicCompletions/completion.$shell"
          if [[ ! -s $runicCompletions/completion.$shell ]]; then
            echo "error: $out/bin/${completions'.program} ${toString completions'.command} $shell printed no completions." >&2
            exit 1
          fi
          installShellCompletion --cmd ${lib.escapeShellArg completions'.program} "--$shell" "$runicCompletions/completion.$shell"
        done
        rm -r "$runicCompletions"
      ''}
      ${args.postFixup or ""}
    '';

    passthru =
      passthru
      // lib.optionalAttrs (frontend' != null) { frontendNodeModules = frontend'.nodeModules; }
      // {
        inherit host nativeAot testRunner;
        # nix run .#<package>.fixedOutputHashes: the hashes of frontend.hash and dotnetTools.hashes, all in one run.
        fixedOutputHashes = callPackage ./fixed-output-hashes.nix { } {
          inherit pname;
          frontend = if frontend' == null then { } else frontend'.fakeHashDownloads;
          frontendPerSystem = frontend' != null && frontend'.hashPerSystem;
          tools = if dotnetTools' == null then { } else dotnetTools'.fakeHashDownloads;
        };
      };

    meta = {
      platforms = lib.platforms.linux;
    }
    // meta;
  }
)
