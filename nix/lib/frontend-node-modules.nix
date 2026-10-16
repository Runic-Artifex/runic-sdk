# The frontend's node_modules, installed offline from its lock file. The result has node_modules at its root,
# with patched shebangs, for RunicApplicationFrontendNodeModulesDirectory.
#
#   npm:  without a hash, importNpmLock fetches every package from package-lock.json by its integrity;
#         with a hash, fetchNpmDeps fetches the npm cache as one fixed-output derivation.
#   pnpm: fetchPnpmDeps fetches the pnpm store (a hash is required) and pnpmConfigHook installs offline.
#   Bun:  `bun install --frozen-lockfile` for the system's OS and CPU in a fixed-output derivation (a hash is
#         required), then a copy with patched shebangs, which a fixed-output derivation cannot contain. The copy
#         fails when the lock file changed but the hash did not.
{
  lib,
  stdenv,
  stdenvNoCC,
  cacert,
  importNpmLock,
  fetchNpmDeps,
  npmHooks,
  fetchPnpmDeps,
  pnpmConfigHook,
  pnpm,
  bun,
  nodejs,
  writableTmpDirAsHomeHook,
}:
{
  pname,
  version,
  # The frontend directory: its package.json and lock file, without the rest of the application.
  src,
  packageManager,
  # A hash, or { <system> = <hash>; } for a hash that differs per system, such as Bun's.
  hash ? null,
  pnpmFetcherVersion ? 4,
}:
let
  name = "${pname}-frontend-node-modules";
  system = stdenvNoCC.hostPlatform.system;
  hashes = if lib.isAttrs hash then hash else null;
  hash' =
    if hashes == null then
      hash
    else
      hashes.${system} or (throw ''
        buildRunicApplication: frontend.hash has no hash for ${system}, only for ${lib.concatStringsSep ", " (lib.attrNames hashes)}.
        Add ${system} = lib.fakeHash; and build: Nix reports the hash.'');
  install = ''
    runHook preInstall
    mkdir -p $out
    if [[ -d node_modules ]]; then cp -a node_modules $out/; else mkdir $out/node_modules; fi
    runHook postInstall
  '';
  requireHash =
    assert lib.assertMsg (hash' != null)
      "buildRunicApplication: frontend.hash is required for ${packageManager}; set it to lib.fakeHash, build, and copy the hash Nix reports.";
    hash';
in
if packageManager == "npm" && hash' == null then
  importNpmLock.buildNodeModules {
    npmRoot = src;
    inherit nodejs;
    derivationArgs = {
      pname = name;
      inherit version;
      npmFlags = [ "--ignore-scripts" ];
      # The installed tree, not the package.json and lock that buildNodeModules copies beside it.
      postInstall = ''
        rm -f $out/package.json $out/package-lock.json
        mkdir -p $out/node_modules
      '';
    };
  }
else if packageManager == "npm" then
  stdenv.mkDerivation {
    pname = name;
    inherit version src;
    npmDeps = fetchNpmDeps {
      name = "${name}-npm-deps";
      inherit src;
      hash = requireHash;
    };
    npmFlags = [ "--ignore-scripts" ];
    nativeBuildInputs = [
      nodejs
      npmHooks.npmConfigHook
    ];
    dontBuild = true;
    installPhase = install;
  }
else if packageManager == "pnpm" then
  stdenv.mkDerivation {
    pname = name;
    inherit version src;
    pnpmDeps = fetchPnpmDeps {
      pname = name;
      inherit version src pnpm;
      hash = requireHash;
      fetcherVersion = pnpmFetcherVersion;
    };
    nativeBuildInputs = [
      nodejs
      pnpm
      pnpmConfigHook
      writableTmpDirAsHomeHook
    ];
    dontBuild = true;
    installPhase = install;
  }
else if packageManager == "bun" then
  let
    # Bun installs the optional packages of one operating system and CPU, which it is told rather than detects,
    # so the dependencies of another system can be installed, and their hash computed, on this one.
    bunCpu = {
      x86_64-linux = "x64";
      aarch64-linux = "arm64";
    };
    fetchedFor =
      target: outputHash:
      stdenvNoCC.mkDerivation {
        # The output path depends only on its name and hash: name the Bun version, so another Bun installs again
        # rather than reusing what an earlier one installed.
        pname = "${name}-bun-${bun.version}-install";
        inherit version src;
        nativeBuildInputs = [
          bun
          cacert
          writableTmpDirAsHomeHook
        ];
        impureEnvVars = lib.fetchers.proxyImpureEnvVars;
        buildPhase = ''
          runHook preBuild
          export BUN_INSTALL_CACHE_DIR=$(mktemp -d)
          bun install --frozen-lockfile --ignore-scripts --no-progress --no-summary --backend=copyfile \
            --os=linux --cpu=${
              bunCpu.${target} or (throw "buildRunicApplication: Bun frontend dependencies for ${target} are not supported")
            }
          # Bun's own bookkeeping and the cache-specific files differ between runs.
          rm -rf node_modules/.cache node_modules/.bun-tag
          runHook postBuild
        '';
        # With the files they were installed from, which the copy below compares with the frontend's: a changed
        # lock file with the previous hash names this same output, which Nix would otherwise reuse unchanged.
        installPhase = ''
          runHook preInstall
          mkdir -p $out
          if [[ -d node_modules ]]; then cp -a node_modules $out/; else mkdir $out/node_modules; fi
          cp -r --no-preserve=mode "$src" $out/installed-from
          runHook postInstall
        '';
        dontFixup = true;
        inherit outputHash;
        outputHashMode = "recursive";
      };
    fetched = fetchedFor system requireHash;
  in
  stdenv.mkDerivation {
    pname = name;
    inherit version src;
    dontUnpack = true;
    nativeBuildInputs = [ nodejs ];
    dontBuild = true;
    installPhase = ''
      runHook preInstall
      if ! diff -r ${fetched}/installed-from "$src"; then
        echo "error: frontend.hash is out of date: package.json or bun.lock changed since ${fetched} was installed with it. Set the hash to lib.fakeHash, build, and copy the hash Nix reports." >&2
        exit 1
      fi
      mkdir -p $out
      cp -a ${fetched}/node_modules $out/
      chmod -R u+w $out/node_modules
      patchShebangs $out/node_modules
      runHook postInstall
    '';
    dontFixup = true;
    passthru = {
      inherit fetched;
      # The fixed-output install for each system that hash names, for computing another system's hash here:
      # nix build .#default.frontendNodeModules.fetchedFor.aarch64-linux
      fetchedFor = lib.mapAttrs fetchedFor (if hashes == null then { ${system} = requireHash; } else hashes);
    };
  }
else
  throw "buildRunicApplication: frontend.packageManager must be npm, pnpm or bun, not ${toString packageManager}"
