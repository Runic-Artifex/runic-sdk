![Runic Toolkit banner](.github/assets/brand/banner.png)

# Runic Toolkit

Build a native desktop application with a modern web frontend without giving up a typed .NET application boundary. Runic Toolkit supplies the NativeAOT-friendly host, CS-WebUI integration, Application Bridge, and project templates; you bring your domain and frontend.

## Start in five minutes

Prerequisites: the .NET 10 SDK, Node.js 24.18 or later, npm, and a supported desktop platform for [CS-WebUI](https://github.com/Runic-Artifex/cs-webui).

```bash
dotnet new install RunicToolkit.Templates::<VERSION>
dotnet new runic-toolkit-svelte --name MyApp
cd MyApp
dotnet run
```

Replace `<VERSION>` with the current preview shown on [NuGet](https://www.nuget.org/packages/RunicToolkit.Templates). Choose `runic-toolkit-react`, `runic-toolkit-vue`, or `runic-toolkit-angular` to start with another frontend. The templates include a working counter, a generated Application Bridge contract, and the matching frontend runtime. Run `dotnet run -- --smoke-test` to exercise the managed bridge without opening a window.

## Choose your package

| Need | Start with |
| --- | --- |
| A complete native desktop app | `RunicToolkit.Templates` |
| Typed browser-to-.NET commands and events | `RunicToolkit.ApplicationBridge` and `@runic-artifex/application-bridge` |
| Generated, trim-friendly bridge contracts | `RunicToolkit.ApplicationBridge.Generators` |
| A deterministic app lifecycle | `RunicToolkit.Hosting` |
| Microsoft Generic Host integration | `RunicToolkit.Hosting.GenericHost` |
| A static frontend and root-session boundary | `RunicToolkit.Hosting.WebUi` |
| A native CS-WebUI window | `RunicToolkit.Hosting.CsWebUi` |
| The high-level CS-WebUI app builder | `RunicToolkit.Hosting.CsWebUi.App` |
| The Application Bridge over CS-WebUI | `RunicToolkit.Hosting.CsWebUi.ApplicationBridge` |
| Deterministic frontend asset manifests | `RunicToolkit.Hosting.Build` |
| Desktop capability interfaces | `RunicToolkit.Desktop` |
| Observable range collections | `RunicToolkit.Collections` |
| Closed registration source generation | `RunicToolkit.Hosting.Generators` |
| Development diagnostics and frontend watch | `RunicToolkit.DotNet.RunicToolkit` |

Install NuGet packages with `dotnet add package <PackageId> --prerelease`; install the TypeScript runtime with `npm install @runic-artifex/application-bridge`. All currently published Toolkit packages are preview releases—keep the matched template package versions together until the first stable release.

## Learn and get help

Start with the [documentation](https://github.com/Runic-Artifex/runic-toolkit/tree/main/docs) and [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples). The Application Bridge is schema-first and deliberately has no generic property-setting or numeric-member compatibility API. Its protocol, validation, determinism, and NativeAOT guidance live in the [Application Bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md).

Report bugs or request features in [GitHub Issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Runic Toolkit is released under the [MIT License](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
