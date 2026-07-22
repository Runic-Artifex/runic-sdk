# Dependency Notices packed-package verifier

This executable is intentionally parameterized because the packaging projects may be packed at any release version and output location. It verifies the exact local nupkgs before consuming them:

- Core, Engine, Rendering, Runtime, Tool, and Build IDs and versions;
- expected `lib/net10.0` or `tools/net10.0/any` assets;
- all eight Tool compile-time dependency assemblies bundled under `tools/net10.0/any`;
- managed assembly identity, public `WebUIToolkit.DependencyNotices.*` API ownership, required library dependency edges, and the Tool's bundled Core/Engine assemblies;
- safe ZIP paths, bounded expanded content, and absence of project/restore-state files;
- feed-only restore into an empty cache, package-reference build/run, actual dotnet-tool install/run, and operational imported Build `Generate` then `Verify` targets using that explicit local tool path;
- optional Native-AOT publish/run with an isolated ignored `obj/aot.packages.lock.json`.

The verifier does not publish packages. A caller first packs all six package IDs at one version into a local feed, then runs `samples/DependencyNotices.PackageConsumer/verify.ps1`.
