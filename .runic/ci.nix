# The SDK's Linux pull-request jobs for runic-ci (https://github.com/Runic-Artifex/runic-ci,
# docs/repo-contract.md). Each job runs the same command or eng/ci/jobs script as the
# matching .github/workflows/ci.yml job, with its environment from Nix instead of
# apt and actions. eng/ci/plan.mjs selects the jobs, as it selects ci.yml's.
#
# Tiers: "sandbox" jobs build offline in the Nix sandbox and are cached by their inputs.
# "nspawn" jobs run in a disposable container: those that reach package registries or
# GitHub's test images, and the Polkit suite, which needs a root-owned /proc.
{ ci }:
let
  inherit (ci) pkgs lib root;
  fs = lib.fileset;
  path = p: root + "/${p}";

  shell = ci.flake.devShells.${ci.system}.default;
  tool = ci.tool shell;
  dotnet = tool "dotnet-sdk-wrapped";
  bun = tool "bun";
  nodejs = tool "nodejs";
  npm = tool "npm";
  pnpm = tool "pnpm";
  chromium = tool "chromium";
  actionlint = tool "actionlint";
  # NativeAOT links with clang, which Ubuntu runners preinstall.
  clang = tool "clang-wrapper";

  # Sources. A job sees only the files it reads, so a change elsewhere leaves its result cached.
  notBuildInput = fs.unions (map path [ "docs" ".github" "nixos" "nix" ".runic" ]);
  rootFiles = fs.unions (lib.mapAttrsToList (name: _: path name)
    (lib.filterAttrs (name: type: type == "regular" && !lib.hasPrefix "flake." name) (builtins.readDir root)));
  # Web packages other than the Views runtime, which the .NET build bundles. Their manifests
  # stay because bun.lock names them.
  otherWebSources = fs.difference
    (fs.unions (map (name: path "packages/web/${name}")
      (lib.filter (name: name != "views") (lib.attrNames (builtins.readDir (path "packages/web"))))))
    (fs.fileFilter (file: file.name == "package.json") (path "packages/web"));
  sources = {
    full = ci.source root;
    core = ci.source (fs.difference root notBuildInput);
    managed = ci.source (fs.difference root (fs.union notBuildInput otherWebSources));
    web = ci.source (fs.unions [ rootFiles (path "eng") (path "specs") (path "packages/web") (path "tests/web") ]);
  };

  # NuGet: one fixed-output fetch per package from the committed lock files (nix/lib), plus the
  # PackageDownload items no lock records (.runic/nuget-extras.json, .runic/update-nuget-extras).
  lockFiles = lib.filter (file:
    let relative = lib.removePrefix "${toString root}/" (toString file); in
    builtins.match "(.*/)?packages(\\.[^/]+)?\\.lock\\.json" relative != null && !lib.hasPrefix "tests/fixtures/nix/" relative)
    (lib.filesystem.listFilesRecursive root);
  nugetLock = pkgs.callPackage (path "nix/lib/nuget-lock-deps.nix") { inherit (pkgs.dotnetCorePackages) fetchNupkg; } {
    inherit lockFiles;
    dotnet-sdk = dotnet;
  };
  extras = lib.importJSON ./nuget-extras.json;
  nuget = ci.nugetFolder {
    packages = nugetLock.packages ++ (dotnet.packages or [ ])
      ++ map (entry: pkgs.dotnetCorePackages.fetchNupkg { inherit (entry) pname version; hash = "sha512-${entry.sha512}"; }) extras;
    locked = nugetLock.lockedPackages ++ map (entry: "${lib.toLower entry.pname} ${lib.toLower entry.version} ${entry.sha512}") extras;
  };

  # The workspace's node_modules from bun.lock. After a bun.lock change, set .runic/node-modules.sri
  # to lib.fakeHash, build .#nodeModules, and copy the hash Nix reports.
  nodeModules = ci.bunWorkspace {
    name = "runic-workspace";
    manifests = ci.source (fs.unions ([ (path "package.json") (path "bun.lock") ]
      ++ map (p: path "${p}/package.json") (lib.importJSON (path "package.json")).workspaces));
    hash = lib.fileContents ./node-modules.sri;
    inherit bun nodejs;
  };

  environment = {
    CONFIGURATION = "Release";
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
    DOTNET_ROOT = "${dotnet}/share/dotnet";
    MSBUILDDISABLENODEREUSE = "1";
    DisableImplicitLibraryPacksFolder = "true";
    # Reproducible outputs, so jobs reusing an unchanged build are cache hits: no commit id in
    # versions or source links of CI builds. Release builds keep it.
    EnableSourceControlManagerQueries = "false";
    FONTCONFIG_FILE = ci.fonts;
    PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH = "${chromium}/bin/chromium";
  };

  # A job with the SDK toolchain, NuGet packages and node_modules. Sandbox jobs restore offline:
  # nuget.org becomes an empty local source (vulnerability audits run in dependency-audit.yml).
  sdkJob =
    { name, tier ? "sandbox", nugetPackages ? true, packages ? [ ], env ? { }, setup ? "", ... }@args:
    ci.job (removeAttrs args [ "nugetPackages" ] // {
      inherit name tier;
      # Package managers install prebuilt native tools (pnpm, esbuild) that expect /lib64's loader.
      fhsLoader = tier == "nspawn";
      # The pinned npm and pnpm first: Node bundles an older npm (setup-sdk installs these globally).
      packages = [ npm pnpm dotnet bun nodejs chromium clang pkgs.python3 pkgs.procps pkgs.curl pkgs.jq ] ++ packages;
      env = environment // env;
      setup = lib.optionalString nugetPackages ''
        export NUGET_PACKAGES=$NIX_BUILD_TOP/nuget
        cp -a ${nuget} "$NUGET_PACKAGES"
        chmod -R u+w "$NUGET_PACKAGES"
      '' + lib.optionalString (nugetPackages && tier == "sandbox") ''
        mkdir -p "$NIX_BUILD_TOP/nuget-source"
        sed -i 's#value="https://api.nuget.org/v3/index.json" protocolVersion="3"#value="'"$NIX_BUILD_TOP"'/nuget-source"#' NuGet.config
      '' + ''
        # btrfs: reflink copies of node_modules cost no data writes.
        cp -a --reflink=auto ${nodeModules}/. .
        chmod u+w . packages packages/web packages/web/*
        chmod -R u+w node_modules packages/web/*/node_modules 2>/dev/null || true
      '' + setup;
    });

  # The build outputs eng/ci/build-artifact.mjs archives, as $out/tree. Incremental-build state
  # that records build times or random names is left out, so an unchanged build is reproducible.
  exportBuildTree = ''
    bun -e 'import { buildPaths } from "./eng/ci/build-artifact.mjs"; console.log(buildPaths().join("\0"))' \
      | tr -d '\n' > "$NIX_BUILD_TOP/build-paths"
    mkdir -p "$out/tree"
    xargs -0 -a "$NIX_BUILD_TOP/build-paths" cp -a --reflink=auto --parents -t "$out/tree"
    find "$out/tree" \( -name '*.AssemblyReference.cache' -o -name runic-frontend-www.files \
      -o -name .runic-bridge-generation-cache.json -o -name '*_wpftmp*' -o -path '*/obj/runic-bridge/bootstrap' \
      -o -path '*/obj/*.pdb' \) -prune -exec rm -rf {} +
  '';

  build-core = sdkJob {
    name = "build-core";
    github = "Build shared SDK inputs";
    src = sources.managed;
    script = ''
      dotnet restore RunicSdk.Core.slnx --locked-mode
      bun eng/run.mjs build-core
      ${exportBuildTree}
    '';
  };
  build-web = sdkJob {
    name = "build-web";
    github = "Build shared SDK inputs";
    src = sources.web;
    nugetPackages = false;
    script = ''
      bun eng/run.mjs build-web
      ${exportBuildTree}
    '';
  };
  # The build job's package candidates, which package consumers and templates install.
  packages = sdkJob {
    name = "packages";
    github = "Build shared SDK inputs";
    src = sources.full;
    uses = [ build-core build-web ];
    # Packages record their source revision; the job's synthetic commit is the same for the same tree.
    env.EnableSourceControlManagerQueries = "true";
    script = ''
      bun eng/run.mjs pack-built
      bun eng/release/verify-template-locks.mjs
      mkdir -p "$out/tree/artifacts"
      cp -a artifacts/packages "$out/tree/artifacts/"
    '';
  };

  # eng/ci/plan.mjs managedGroups; ci.yml's managed job runs one step per group.
  managed = lib.genAttrs [ "platform" "assets" "navigation" "application" ] (group: sdkJob {
    name = "managed-${group}";
    github = "Managed tests";
    plan = { job = "managed"; inherit group; };
    src = sources.managed;
    uses = [ build-core ];
    # Runic.Platform.Runtime.Tests sets a set-group-ID bit, which the sandbox refuses, and the
    # Polkit suite needs a root-owned /proc, which no user namespace shows (.runic ledger L6, L7).
    tier = if group == "platform" then "nspawn" else "sandbox";
    userNamespace = group != "platform";
    packages = lib.optionals (group == "application") [ pkgs.clang pkgs.zlib ];
    script = "bun eng/ci/test-managed.mjs ${group}";
    collect = [ "artifacts/test-results" ];
  });

  # eng/ci/plan.mjs webTests(); ci.yml's web job runs one step per package.
  web = lib.listToAttrs (map (item:
    let package = lib.last (lib.splitString "/" item.path); in
    lib.nameValuePair package (sdkJob {
      name = "web-${package}";
      github = "Web packages and framework consumers";
      plan = { job = "web"; inherit package; };
      src = sources.web;
      nugetPackages = false;
      uses = [ build-web ];
      # The isolated-consumer test installs the packed package from the npm registry (ledger L8).
      tier = if package == "vite-plugin-runic" then "nspawn" else "sandbox";
      network = package == "vite-plugin-runic";
      script = "cd ${item.path} && bun run --bun test";
    }))
    (lib.filter (item: (lib.importJSON (path "${item.path}/package.json")).scripts ? test)
      (lib.importJSON (path "eng/workspace.json")).npm));

  frameworks = sdkJob {
    name = "frameworks";
    github = "Web packages and framework consumers";
    plan = { job = "web"; frameworks = true; };
    src = sources.web;
    nugetPackages = false;
    uses = [ build-web ];
    # The isolated npm consumers install from the registry.
    tier = "nspawn";
    network = true;
    script = ''
      (cd packages/web/svelte && bun run --bun check)
      (cd packages/web/sveltekit && bun run --bun check)
      bun tests/web/svelte-package-consumers/package-consumers.mjs
    '';
  };

  engineering = sdkJob {
    name = "engineering";
    github = "engineering";
    src = sources.full;
    uses = [ build-core ];
    packages = [ actionlint ];
    script = ''
      bun test --timeout 180000 ./eng/*.test.mjs ./eng/ci/*.test.mjs ./eng/release/*.test.mjs ./eng/reliability/*.test.mjs ./tests/engineering/*.test.mjs
      bun tools/dotnet-runic/metadata/generate.mjs --check
      bun eng/generate-shipping-projects.mjs --check
      actionlint -color
    '';
  };
  # The devShell's desktop environment (GTK, WebKitGTK, D-Bus, portals, GStreamer and the
  # variables its shellHook sets), plus what ci.yml installs with apt on Ubuntu.
  # nixpkgs' xvfb-run, patched for runic-ci's containers (.runic/xvfb-run.py).
  xvfb-run = pkgs.runCommand "xvfb-run-ci" { nativeBuildInputs = [ pkgs.python3 ]; } ''
    mkdir -p $out/bin
    python3 -I ${./xvfb-run.py} ${pkgs.xvfb-run}/bin/.xvfb-run-wrapped $out/bin/.xvfb-run-wrapped
    sed "s|${pkgs.xvfb-run}/bin/.xvfb-run-wrapped|$out/bin/.xvfb-run-wrapped|" ${pkgs.xvfb-run}/bin/xvfb-run > $out/bin/xvfb-run
    chmod +x $out/bin/xvfb-run $out/bin/.xvfb-run-wrapped
  '';

  desktop = rec {
    libraries = with pkgs; [ xorg.libXtst xorg.libX11 xorg.libXi libsecret ];
    # The tolerant xvfb-run first, before the devShell's.
    packages = [ xvfb-run ] ++ shell.nativeBuildInputs ++ (with pkgs; [
      xdotool gnome-keyring libsecret glib.bin bubblewrap xdg-dbus-proxy dbus
    ]);
    # System files Ubuntu's packages install, which D-Bus and GLib read from fixed paths.
    etc = {
      # nixpkgs' session.conf includes this path for local changes; leave that include out. Buses
      # listen on files in the container's /tmp, not abstract sockets, which network jobs would
      # share with the host's network namespace.
      "dbus-1/session.conf" = pkgs.runCommand "dbus-session.conf" { } ''
        grep -v '/etc/dbus-1/session' ${pkgs.dbus}/share/dbus-1/session.conf \
          | sed 's|<listen>unix:tmpdir=/tmp</listen>|<listen>unix:dir=/tmp</listen>|' > $out
        grep -q '<listen>unix:dir=/tmp</listen>' $out
      '';
      "machine-id" = pkgs.writeText "machine-id" "9a5d3c1e0f4b4c8d9e2f7a6b5c4d3e2f\n";
    };
    setup = ''
      # The shellHook expands variables that may be unset.
      set +u
      ${shell.shellHook}
      set -u
      # Libraries tests load with dlopen that Ubuntu's apt packages bring along (XTEST for xdotool).
      export LD_LIBRARY_PATH=${lib.makeLibraryPath desktop.libraries}:$LD_LIBRARY_PATH
      # Data and D-Bus service files (org.a11y.Bus, portals, keyring), which Ubuntu installs in /usr/share.
      export XDG_DATA_DIRS=$XDG_DATA_DIRS:${lib.makeSearchPath "share" (map (p: p.out or p) desktop.packages)}
      export NUGET_PACKAGES=$NIX_BUILD_TOP/nuget
      export LOCALE_ARCHIVE=${pkgs.glibcLocales}/lib/locale/locale-archive
      # A user session bus at $XDG_RUNTIME_DIR/bus, as a runner's systemd user session provides.
      # Without one, GLib autolaunches dbus-launch, which outlives the processes that started it.
      export XDG_RUNTIME_DIR=$NIX_BUILD_TOP/runtime
      mkdir -m 0700 -p "$XDG_RUNTIME_DIR"
      dbus-daemon --session --address="unix:path=$XDG_RUNTIME_DIR/bus" --fork --nopidfile
    '';
  };

  # ci.yml's views and package-consumers jobs install no WebKitGTK, so their windows open in the
  # browser. Without a display, an installed WebKitGTK makes the hosts exit instead (ledger L12).
  browserOnly = {
    packages = [ xvfb-run ];
    etc = { };
  };

  # Jobs from GitHub images, run as root in the image as ci.yml's `docker run` steps do.
  fedora = ci.image {
    imageName = "docker.io/library/fedora";
    imageDigest = "sha256:43b29f65a41eb9c35e1cd5323e3bdf3b655c2357a9f4f1ff2f9c2798e5045d80";
    sha256 = lib.fileContents ./images/fedora.sri;
  };
  alpine = ci.image {
    imageName = "docker.io/library/alpine";
    imageDigest = "sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8";
    sha256 = lib.fileContents ./images/alpine.sri;
  };

  views = sdkJob {
    name = "views";
    github = "Window and View journeys";
    plan = { job = "views"; };
    src = sources.full;
    uses = [ build-core build-web ];
    tier = "nspawn";
    network = true;
    inherit (browserOnly) packages etc;
    script = "bash eng/ci/jobs/views.sh";
  };

  package-consumers = sdkJob {
    name = "package-consumers";
    github = "package-consumers";
    plan = { job = "package-consumers"; };
    src = sources.full;
    uses = [ packages ];
    tier = "nspawn";
    network = true;
    inherit (browserOnly) packages etc;
    script = "bash eng/ci/jobs/package-consumers.sh";
  };

  # ci.yml's templates matrix: two lanes of two frameworks; the second also runs after the first failed.
  templates = lib.listToAttrs (map ({ first, second, creator ? "0", desktopSmoke ? "0" }:
    lib.nameValuePair "templates-${first}-${second}" (sdkJob {
      name = "templates-${first}-${second}";
      github = "Templates / ${first}+${second}";
      plan = { job = "templates"; };
      src = sources.full;
      uses = [ packages ];
      tier = "nspawn";
      network = true;
      # Only the lane with the desktop smoke installs the desktop packages on GitHub.
      inherit (if desktopSmoke == "1" then desktop else browserOnly // { setup = ""; }) packages setup etc;
      script = ''
        status=0
        RUNIC_TEMPLATE_FRAMEWORKS=${first} RUNIC_TEMPLATE_CREATOR=${creator} RUNIC_TEMPLATE_DESKTOP_SMOKE=${desktopSmoke} bun run verify:templates || status=$?
        RUNIC_TEMPLATE_FRAMEWORKS=${second} RUNIC_TEMPLATE_CREATOR=0 RUNIC_TEMPLATE_DESKTOP_SMOKE=0 bun run verify:templates || status=$?
        exit "$status"
      '';
    }))
    [ { first = "react"; second = "svelte"; creator = "1"; } { first = "vue"; second = "angular"; desktopSmoke = "1"; } ]);

  native-linux-x64 = sdkJob {
    name = "native-linux-x64";
    github = "Native / linux-x64";
    plan = { job = "native"; leg = "linux-x64"; };
    src = sources.full;
    tier = "nspawn";
    network = true;
    inherit (desktop) packages setup etc;
    env.RID = "linux-x64";
    # The NativeAOT Polkit suite needs a root-owned /proc (ledger L6), as on a GitHub runner.
    userNamespace = false;
    script = "bash eng/ci/jobs/native-linux.sh";
    collect = [ "artifacts/native-tests" "artifacts/accessibility" ];
  };

  # W315-011: the real xdg-desktop-portal and its GNOME backend, in Fedora (eng/ci/real-portal.sh).
  native-real-portal = sdkJob {
    name = "native-real-portal";
    github = "Native / linux-x64";
    plan = { job = "native"; leg = "linux-x64"; };
    src = sources.full;
    tier = "nspawn";
    network = true;
    image = fedora;
    prepare = ''
      dotnet publish tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -r linux-x64 --self-contained true -o artifacts/portal-jit
      dotnet publish tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true -o artifacts/portal-aot
    '';
    # The published tests run with their Nix libraries (Fedora's glibc is older) and use Fedora's
    # portal, document portal and GNOME backend over the session bus.
    imageStore = true;
    packages = [ pkgs.clang pkgs.zlib ];
    script = "sh eng/ci/real-portal.sh artifacts/portal-jit/Runic.Platform.Linux.Portal.Tests artifacts/portal-aot/Runic.Platform.Linux.Portal.Tests";
  };

  # The musl native checks in Alpine (eng/ci/native-musl.sh), as ci.yml's native-musl job runs them.
  native-musl-x64 = ci.job {
    name = "native-musl-x64";
    github = "Native / linux-musl-x64";
    plan = { job = "native-musl"; };
    src = sources.full;
    tier = "nspawn";
    network = true;
    image = alpine;
    # Its NativeAOT Polkit suite needs a root-owned /proc (ledger L6), as under Docker on GitHub.
    userNamespace = false;
    env = { NUGET_PACKAGES = "/build/source/.cache/nuget"; GITHUB_ACTIONS = "true"; };
    script = "sh eng/ci/native-musl.sh linux-musl-x64";
  };

  # Not a PR job yet: package.mjs publishes with NativeAOT on the host and copies the result into
  # the Flatpak, so the host's glibc must not be newer than the GNOME runtime's. Nix's (2.44) is
  # (ledger L14); Ubuntu 24.04's is not, so Packaging / flatpak stays on GitHub.
  packaging-flatpak = sdkJob {
    name = "packaging-flatpak";
    github = "Packaging / flatpak";
    plan = { job = "packaging"; leg = "flatpak"; };
    src = sources.full;
    tier = "nspawn";
    network = true;
    inherit (desktop) etc;
    packages = desktop.packages ++ (with pkgs; [ flatpak flatpak-builder appstream desktop-file-utils librsvg ]);
    setup = desktop.setup + ''
      flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
    '';
    script = "bash eng/ci/jobs/packaging-flatpak.sh";
  };

  # nix.yml: the flake's checks and the NixOS VM test of a packaged application (needs kvm).
  nixJobs = lib.mapAttrs' (name: drv: lib.nameValuePair "nix-${name}"
    (ci.derivation drv { name = "nix-${name}"; github = "flake-check"; plan = { job = "nix"; }; }))
    ci.flake.checks.${ci.system}
  // {
    nixos-vm-packaging = ci.derivation ci.flake.packages.${ci.system}.packaging-nixos {
      name = "nixos-vm-packaging"; github = "nixos-vm"; plan = { job = "nix"; };
    };
  };
in
{
  apiVersion = 1;
  jobs = {
    inherit build-core build-web packages frameworks engineering views package-consumers
      native-linux-x64 native-real-portal native-musl-x64;
  }
  // templates // nixJobs
  // lib.mapAttrs' (group: job: lib.nameValuePair "managed-${group}" job) managed
  // lib.mapAttrs' (package: job: lib.nameValuePair "web-${package}" job) web;
  # How runic-ci plans a change, as ci.yml's plan job does: the base checkout's planner judges
  # the changed files (NUL-separated on stdin), then the head checkout's planner enumerates.
  planner = {
    tools = [ bun ];
    affected = "bun eng/ci/plan.mjs --affected";
    plan = "bun eng/ci/plan.mjs --plan";
    all = "bun eng/ci/plan.mjs --all";
  };
  packages = {
    inherit nodeModules nuget;
    nodeModulesFetched = nodeModules.fetched;
  };
}
