# WebUIToolkit.DependencyNotices.Build

This package contains declarative `buildTransitive` props and targets only. It never restores a tool, downloads evidence, or duplicates notice-generation logic.

Consumers opt in and supply the path of a separately installed `WebUIToolkit.DependencyNotices.Tool` executable:

```xml
<PropertyGroup>
  <DependencyNoticesEnabled>true</DependencyNoticesEnabled>
  <DependencyNoticesToolPath>/explicit/path/to/dependency-notices</DependencyNoticesToolPath>
  <DependencyNoticesMode>Verify</DependencyNoticesMode>
  <DependencyNoticesRoot>$(MSBuildProjectDirectory)</DependencyNoticesRoot>
  <DependencyNoticesConfig>dependency-notices.input.json</DependencyNoticesConfig>
  <DependencyNoticesOutputDirectory>$(MSBuildProjectDirectory)/obj/dependency-notices/</DependencyNoticesOutputDirectory>
  <DependencyNoticesArtifactName>Example.Product</DependencyNoticesArtifactName>
  <DependencyNoticesArtifactVersion>1.2.3</DependencyNoticesArtifactVersion>
</PropertyGroup>
```

Supported modes map directly to the CLI contract: `Generate` creates the configured outputs and `Verify` regenerates and byte-compares the same configured output set. The package deliberately has no acquisition mode.

## Locked NuGet consumer evidence

An application that ships a restored package graph can opt into the bounded consumer-evidence bridge by explicitly supplying the exact lock file, assets file, target framework, and local package root. The Build package does not restore, inspect feeds, or infer any of these locations.

```xml
<PropertyGroup>
  <DependencyNoticesNuGetLock>$(MSBuildProjectDirectory)/packages.lock.json</DependencyNoticesNuGetLock>
  <DependencyNoticesNuGetAssets>$(BaseIntermediateOutputPath)project.assets.json</DependencyNoticesNuGetAssets>
  <DependencyNoticesNuGetFramework>net10.0</DependencyNoticesNuGetFramework>
  <DependencyNoticesNuGetPackagesRoot>$(NuGetPackageRoot)</DependencyNoticesNuGetPackagesRoot>
</PropertyGroup>
```

The tool reads only these already-restored local inputs, verifies their lock/assets agreement, and requires local UTF-8 license evidence from every graph component. Caller-supplied Text Resources packs remain manual components in `dependency-notices.input.json`: declare their canonical PURL, revision, local SHA-256-pinned evidence, and origin there. This bridge never downloads, extracts, signs, or otherwise interprets those packs.
