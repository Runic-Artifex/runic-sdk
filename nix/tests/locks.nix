# Lock files and dependency hashes across changes and systems, from tests/fixtures/nix:
#
# - A Bun frontend whose lock file changed while its hash did not fails. Nix names a fixed-output derivation by
#   its name and hash alone, so the install of the earlier lock file is reused; here the copy of the current install
#   is given the changed lock file, as such a reuse does.
# - The Bun install for another system is a fixed-output derivation that this system can build.
# - An npm frontend without a hash in a lib.fileset.toSource source installs from package-lock.json, which the
#   builder reads at evaluation from the unfiltered source: the filtered one's store path exists only once a
#   writable evaluation has added it, so reading from it fails nix flake check --no-build --all-systems on the
#   empty store that the Nix workflow runs it on.
# - nugetLockFiles keyed by runtime identifier selects this runtime's lock files, and a locked restore rejects a
#   lock file with another runtime's section at evaluation.
{ pkgs, runic }:
let
  inherit (pkgs) lib;
  fixtures = ../../tests/fixtures/nix;
  bunFrontend =
    directory:
    runic.frontendNodeModules pkgs {
      pname = "runic-frontend-lock-fixture";
      version = "0.0.0";
      src = fixtures + "/frontend-lock/${directory}";
      packageManager = "bun";
      hash = {
        x86_64-linux = "sha256-hxkqA1xgueouH4HiU7e+giEBNxVB0iEekpE6cXHlLwc=";
        aarch64-linux = "sha256-hxkqA1xgueouH4HiU7e+giEBNxVB0iEekpE6cXHlLwc=";
      };
    };
  current = bunFrontend "current";
  stale = pkgs.testers.testBuildFailure (current.overrideAttrs { src = fixtures + "/frontend-lock/changed"; });

  npmFiltered =
    (runic.buildRunicApplication pkgs {
      pname = "runic-frontend-npm-fixture";
      version = "0.0.0";
      src = lib.fileset.toSource {
        root = fixtures + "/frontend-npm";
        fileset = fixtures + "/frontend-npm/Frontend";
      };
      projectFile = "App.csproj";
      frontend.packageManager = "npm";
      host = null;
    }).frontendNodeModules;

  application =
    nugetLockFiles:
    runic.buildRunicApplication pkgs {
      pname = "runic-lock-fixture";
      version = "0.0.0";
      src = fixtures + "/lock-files";
      projectFile = "App.csproj";
      inherit nugetLockFiles;
      host = null;
    };
  evaluates = nugetLockFiles: (builtins.tryEval (application nugetLockFiles).drvPath).success;
  locks = {
    linux-x64 = fixtures + "/lock-files/linux-x64.lock.json";
    linux-arm64 = fixtures + "/lock-files/linux-arm64.lock.json";
    both = fixtures + "/lock-files/both.lock.json";
  };
in
assert evaluates [ locks.linux-x64 ];
assert evaluates {
  linux-x64 = [ locks.linux-x64 ];
  linux-arm64 = [ locks.linux-arm64 ];
};
assert !evaluates [ locks.both ];
assert !evaluates { linux-arm64 = [ locks.linux-arm64 ]; };
assert !evaluates { linux-x64 = [ locks.linux-arm64 ]; };
pkgs.runCommand "runic-builder-locks" { } ''
  grep -q 'frontend.hash is out of date' ${stale}/testBuildFailure.log
  test -d ${current}/node_modules/is-number
  test -d ${current.fetchedFor.aarch64-linux}/node_modules/is-number
  test -f ${npmFiltered}/node_modules/is-number/package.json
  touch $out
''
