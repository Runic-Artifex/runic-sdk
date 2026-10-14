# NuGet packages from committed packages.lock.json files, for buildDotnetModule's nugetDeps.
#
# A lock entry records the package's contentHash: the SHA-512 of the .nupkg without its signature, which NuGet
# computes for nuget.org's repository-signed packages. Each package is a fixed-output fetch from nuget.org that
# removes the signature (nupkg-unsigned.py) and is checked against that hash, so no generated deps.json is
# needed. Project entries are skipped; a package that appears in several lock files, or under several target
# frameworks and runtime identifiers, is fetched once, and one that the .NET SDK's own packages provide (such as
# Microsoft.NET.ILLink.Tasks) is not fetched. Returns the packages and, for a locked restore, each locked
# package's contentHash.
{
  lib,
  fetchurl,
  python3,
  fetchNupkg,
  dotnetCorePackages,
}:
{
  lockFiles,
  dotnet-sdk ? dotnetCorePackages.sdk_10_0,
}:
let
  key = name: version: "${lib.toLower name}/${lib.toLower version}";
  provided = lib.listToAttrs (
    map (package: lib.nameValuePair (key package.pname package.version) true) dotnet-sdk.packages
  );
  entries = lib.concatMap (
    file:
    let
      lock = lib.importJSON file;
    in
    lib.concatMap (packages: lib.mapAttrsToList (name: entry: entry // { inherit name; }) packages) (
      lib.attrValues (lock.dependencies or { })
    )
  ) (lib.toList lockFiles);
  packages = lib.filter (entry: entry.type or "" != "Project" && entry ? contentHash && entry ? resolved) entries;
  # One entry per package and version. Lock files that record different hashes for it disagree about the package's
  # contents, which no download can satisfy.
  grouped = lib.groupBy (entry: key entry.name entry.resolved) packages;
  locked = lib.mapAttrs (
    name: group:
    let
      hashes = lib.unique (map (entry: entry.contentHash) group);
    in
    assert lib.assertMsg (lib.length hashes == 1)
      "buildRunicApplication: the lock files record different contentHash values for NuGet package ${name}: ${lib.concatStringsSep ", " hashes}. Restore them again from the same source.";
    lib.head group
  ) grouped;
  byKey = removeAttrs locked (lib.attrNames provided);
  fetch =
    entry:
    let
      id = lib.toLower entry.name;
      version = lib.toLower entry.resolved;
      hash = "sha512-${entry.contentHash}";
      src = fetchurl {
        name = "${entry.name}.${entry.resolved}.nupkg";
        url = "https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.${version}.nupkg";
        inherit hash;
        downloadToTemp = true;
        postFetch = ''
          ${python3.interpreter} -I ${./nupkg-unsigned.py} "$downloadedFile" "$out"
        '';
      };
    in
    (fetchNupkg {
      pname = entry.name;
      version = entry.resolved;
      inherit hash;
    }).overrideAttrs
      { inherit src; };
in
{
  # For nugetDeps.
  packages = map fetch (lib.attrValues byKey);
  # "<id> <version> <contentHash>" per locked package, lowercased as in the packages folder, for restoring in
  # locked mode (see build-runic-application.nix).
  lockedPackages = lib.mapAttrsToList (
    name: entry: "${lib.replaceStrings [ "/" ] [ " " ] name} ${entry.contentHash}"
  ) locked;
}
