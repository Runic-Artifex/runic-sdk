# NuGet packages from committed packages.lock.json files, for buildDotnetModule's nugetDeps.
#
# A lock entry records the package's contentHash: the SHA-512 of the .nupkg without its signature, which NuGet
# computes for nuget.org's repository-signed packages. Each package is a fixed-output fetch from nuget.org that
# removes the signature (nupkg-unsigned.py) and is checked against that hash, so no generated deps.json is
# needed. Project entries are skipped; a package that appears in several lock files, or under several target
# frameworks and runtime identifiers, is fetched once, and one that the .NET SDK's own packages provide (such as
# Microsoft.NET.ILLink.Tasks) is not fetched.
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
  byKey = removeAttrs (lib.listToAttrs (
    map (entry: lib.nameValuePair (key entry.name entry.resolved) entry) packages
  )) (lib.attrNames provided);
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
map fetch (lib.attrValues byKey)
