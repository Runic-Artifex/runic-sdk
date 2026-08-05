# RunicToolkit.Frontend.Sdk

MSBuild SDK package for frontend workspace coordination, C#/TypeScript contract
generation, production asset copying, and external compiler integration.

A Node frontend configures its workspace and package directory:

```xml
<PropertyGroup>
  <RunicToolkitFrontendWorkspace>@example/app</RunicToolkitFrontendWorkspace>
  <RunicToolkitFrontendPackageDirectory>$(MSBuildProjectDirectory)/Frontend</RunicToolkitFrontendPackageDirectory>
  <RunicToolkitFrontendOutputDirectory>$(RunicToolkitFrontendPackageDirectory)/dist</RunicToolkitFrontendOutputDirectory>
</PropertyGroup>
```

Set `RunicToolkitFrontendNodeEnabled=false` for a Node-free application. At
least one Node workspace or external compiler must be enabled.

An independent compiler integration sets
`RunicToolkitFrontendCompilerEnabled=true` and maps its manifest, diagnostics,
hot-reload, generated-file, watch-pattern, and build-target properties to the
generic names documented in `docs/guides/frontend-contracts.md`.

The package embeds `RunicToolkit.Frontend.Generators`; consumers do not install
that implementation project directly. `RunicToolkitFrontendBuild=false` and
`RunicToolkitFrontendInstall=false` provide explicit CI/development opt-outs.
