# Dependency Notices package-consumer fixture

This deliberately clean application consumes local NuGet packages, never source projects. Its committed `NuGet.config` clears inherited sources and permits only the staged `.packages` feed plus an optional `.aot-packages` feed for SDK-owned Native-AOT support packages.

Run the repository verifier after packing all shipping projects to one directory:

```powershell
./verify.ps1 -Feed C:\path\to\nupkgs -Version 1.2.3
```

The verifier inspects package metadata and public managed APIs, copies the exact packages to the ignored local feed, restores with empty local caches, builds and runs this consumer, installs and invokes the packed `dependency-notices` tool, and invokes the Build package's imported target with that explicit local tool path. Restore sources remain unavailable throughout execution.

`dependency-notices.input.json` also demonstrates the Wave C external-pack boundary: a caller-supplied Text Resources pack is represented only as one manual component with a canonical PURL, explicit revision, local SHA-256-pinned attribution asset, and review origin. The package is not fetched or parsed here. A first-party consumer that also needs its restored NuGet graph in the notice output supplies the explicit `DependencyNoticesNuGet*` properties documented by the Build package; orchestration owns staging that locked graph and the isolated feed.

Native AOT is opt-in because the SDK may require RID support packs not produced by this repository:

```powershell
./verify.ps1 -Feed C:\path\to\nupkgs -Version 1.2.3 -AotRid win-x64 -AotSupportFeed C:\path\to\sdk-aot-packs
```

The AOT restore uses the ignored `obj/aot.packages.lock.json`. It never writes RID sections to a committed `packages.lock.json`.
