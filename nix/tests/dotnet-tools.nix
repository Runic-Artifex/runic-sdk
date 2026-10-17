# buildRunicApplication's dotnetTools: a build step runs a local tool from a manifest below the source's root, as
# the Translations Vite plugin runs dotnet-runic-translations, from tests/fixtures/nix/dotnet-tools.
#
# - default: src is a path in the flake's source, which has only the files Git tracks, without build output.
# - filtered-source: src is lib.fileset.toSource, and the manifest is found beside the project.
# - manifest-path: src is filtered by builtins.path, and dotnetTools.manifest is a path read as it is.
#
# A filtered source's store path exists only once a writable evaluation has added it, so the last two evaluate
# only when the builder reads the manifest from elsewhere: nix flake check --no-build --all-systems, which the Nix
# workflow runs first on an empty store, evaluates read-only.
{ pkgs, runic }:
let
  inherit (pkgs) lib;
  fixture = ../../tests/fixtures/nix/dotnet-tools;
  hashes.dotnet-runic-translations = "sha256-/sG0ngRIx6fh0LLQzBNvpCk6N1kYB1NYhhKpI6NjDPY=";
  application =
    args:
    runic.buildRunicApplication pkgs (
      {
        pname = "runic-dotnet-tools-fixture";
        version = "0.0.0";
        src = fixture;
        projectFile = "App/App.csproj";
        nugetLockFiles = [ (fixture + "/App/packages.lock.json") ];
        dotnetTools = { inherit hashes; };
        host = null;
        executables = [ "App" ];
        postBuild = ''
          grep -qx 'runic-translations 0.2' App/bin/Release/net10.0/linux-x64/tool-version.txt
        '';
      }
      // args
    );
in
{
  default = application { };
  filtered-source = application {
    pname = "runic-dotnet-tools-filtered-source-fixture";
    src = lib.fileset.toSource {
      root = fixture;
      fileset = lib.fileset.unions [
        (fixture + "/App")
        (fixture + "/Directory.Build.props")
        (fixture + "/Directory.Build.targets")
        (fixture + "/Directory.Packages.props")
      ];
    };
  };
  manifest-path = application {
    pname = "runic-dotnet-tools-manifest-path-fixture";
    src = builtins.path {
      name = "runic-dotnet-tools-manifest-path-source";
      path = fixture;
      filter = path: type: !(lib.elem (baseNameOf path) [ "bin" "obj" ]);
    };
    dotnetTools = {
      manifest = fixture + "/App/.config/dotnet-tools.json";
      inherit hashes;
    };
  };
}
