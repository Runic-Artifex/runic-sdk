# buildRunicApplication's dotnetTools: a build step runs a local tool from a manifest below the source's root, as
# the Translations Vite plugin runs dotnet-runic-translations, from tests/fixtures/nix/dotnet-tools.
{ pkgs, runic }:
let
  fixture = ../../tests/fixtures/nix/dotnet-tools;
in
runic.buildRunicApplication pkgs {
  pname = "runic-dotnet-tools-fixture";
  version = "0.0.0";
  # The flake's source has only the files Git tracks, without build output.
  src = fixture;
  projectFile = "App/App.csproj";
  nugetLockFiles = [ (fixture + "/App/packages.lock.json") ];
  dotnetTools.hashes.dotnet-runic-translations = "sha256-/sG0ngRIx6fh0LLQzBNvpCk6N1kYB1NYhhKpI6NjDPY=";
  host = null;
  executables = [ "App" ];
  postBuild = ''
    grep -qx 'runic-translations 0.2' App/bin/Release/net10.0/linux-x64/tool-version.txt
  '';
}
