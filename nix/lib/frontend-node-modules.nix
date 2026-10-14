# The frontend's node_modules, installed offline from its lock file. The result has node_modules at its root,
# with patched shebangs, for RunicApplicationFrontendNodeModulesDirectory.
#
#   npm:  without a hash, importNpmLock fetches every package from package-lock.json by its integrity;
#         with a hash, fetchNpmDeps fetches the npm cache as one fixed-output derivation.
#   pnpm: fetchPnpmDeps fetches the pnpm store (a hash is required) and pnpmConfigHook installs offline.
#   Bun:  `bun install --frozen-lockfile` in a fixed-output derivation (a hash is required), then a copy
#         with patched shebangs, which a fixed-output derivation cannot contain.
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
  hash ? null,
  pnpmFetcherVersion ? 4,
}:
let
  name = "${pname}-frontend-node-modules";
  install = ''
    runHook preInstall
    mkdir -p $out
    if [[ -d node_modules ]]; then cp -a node_modules $out/; else mkdir $out/node_modules; fi
    runHook postInstall
  '';
  requireHash =
    assert lib.assertMsg (hash != null)
      "buildRunicApplication: frontend.hash is required for ${packageManager}; set it to lib.fakeHash, build, and copy the hash Nix reports.";
    hash;
in
if packageManager == "npm" && hash == null then
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
    fetched = stdenvNoCC.mkDerivation {
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
        bun install --frozen-lockfile --ignore-scripts --no-progress --no-summary --backend=copyfile
        # Bun's own bookkeeping and the cache-specific files differ between runs.
        rm -rf node_modules/.cache node_modules/.bun-tag
        runHook postBuild
      '';
      installPhase = install;
      dontFixup = true;
      outputHash = requireHash;
      outputHashMode = "recursive";
    };
  in
  stdenv.mkDerivation {
    pname = name;
    inherit version;
    dontUnpack = true;
    nativeBuildInputs = [ nodejs ];
    dontBuild = true;
    installPhase = ''
      runHook preInstall
      mkdir -p $out
      cp -a ${fetched}/node_modules $out/
      chmod -R u+w $out/node_modules
      patchShebangs $out/node_modules
      runHook postInstall
    '';
    dontFixup = true;
    passthru = { inherit fetched; };
  }
else
  throw "buildRunicApplication: frontend.packageManager must be npm, pnpm or bun, not ${toString packageManager}"
