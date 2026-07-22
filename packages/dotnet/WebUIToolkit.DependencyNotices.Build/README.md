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
