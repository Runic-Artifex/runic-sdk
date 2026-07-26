# WebUIToolkit.Frontend.Sdk

`WebUIToolkit.Frontend.Sdk` owns the Node workspace build boundary for native
CsWebUi applications. It can restore missing dependencies, verify generated
contracts, select development or production builds, copy the resulting asset
graph into build and publish output, and expose a watch target.

Configure a project with:

```xml
<PropertyGroup>
  <WebUIToolkitFrontendWorkspace>@example/app</WebUIToolkitFrontendWorkspace>
  <WebUIToolkitFrontendPackageDirectory>$(MSBuildProjectDirectory)/../frontend</WebUIToolkitFrontendPackageDirectory>
  <WebUIToolkitFrontendWorkspaceRoot>$(MSBuildProjectDirectory)/..</WebUIToolkitFrontendWorkspaceRoot>
  <WebUIToolkitFrontendContractSource>$(MSBuildProjectDirectory)/frontend.json</WebUIToolkitFrontendContractSource>
  <WebUIToolkitFrontendContractCSharpOutput>$(MSBuildProjectDirectory)/FrontendContract.g.cs</WebUIToolkitFrontendContractCSharpOutput>
  <WebUIToolkitFrontendContractTypeScriptOutput>$(MSBuildProjectDirectory)/../frontend/contract.g.ts</WebUIToolkitFrontendContractTypeScriptOutput>
</PropertyGroup>
```

When the contract properties are present, the SDK verifies that its generated
C# and TypeScript surfaces have not drifted. Regenerate them explicitly with:

```console
dotnet msbuild -t:WebUIToolkitFrontendGenerateContracts
```

`WebUIToolkitFrontendContractVerifyCommand` remains an override point for
applications with another contract compiler.

Production builds pass `--production` to the workspace build script. Frontend
tooling is responsible for writing `webuitoolkit.assets.json`; the SDK copies
that manifest and its hashed assets unchanged into the CsWebUi/VFS web root.
Before copying it removes the previous application asset graph, preventing
obsolete content hashes from accumulating in build or publish output.

Use `dotnet msbuild -t:WebUIToolkitFrontendWatch` for a frontend-owned watch
process. Set `WebUIToolkitFrontendInstall=false` when dependency restoration is
managed outside MSBuild.
