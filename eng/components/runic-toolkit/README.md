![Runic Toolkit banner](.github/assets/brand/banner.png)

# Runic Toolkit

Build a native desktop application with a modern web frontend without giving up a typed .NET application boundary. Runic Toolkit supplies the NativeAOT-friendly application lifecycle, Runic Desktop integration, Application Bridge, and project templates; you bring your domain and frontend.

## Start in five minutes

Prerequisites: the .NET 10 SDK, Node.js 24.18 or later, npm, and a platform supported by [Runic Desktop](https://github.com/Runic-Artifex/runic-desktop).

```bash
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-svelte --name MyApp
cd MyApp
dotnet run
```

Replace `<VERSION>` with the current preview shown on [NuGet](https://www.nuget.org/packages/Runic.Application.Templates). Choose `runic-app-react`, `runic-app-vue`, or `runic-app-angular` to start with another frontend. The templates include a working counter, a generated Application Bridge contract, and the matching frontend runtime. Run `dotnet run -- --smoke-test` to exercise the managed bridge without opening a window.

## Choose your package

| Need | Start with |
| --- | --- |
| A complete native desktop app | `Runic.Application.Templates` |
| Typed browser-to-.NET commands and events | `Runic.Application.Bridge` and `@runic-artifex/application-bridge` |
| A deterministic app lifecycle and generated manifest | `Runic.Application` |
| Microsoft Generic Host integration | `Runic.Application.Hosting` |
| A generated asset and root-session boundary | `Runic.Application` and `Runic.Assets` |
| A managed desktop surface | `Runic.Application.Desktop`, `Runic.Desktop`, and `Runic.Assets.Desktop` |
| Direct compatibility with upstream WebUI | Use the standalone [`CsWebUi`](https://github.com/Runic-Artifex/cs-webui) binding outside Runic Application |
| A deterministic headless test host | `Runic.Application.Testing` |
| Development diagnostics and frontend build coordination | `dotnet-runic` |
| Deterministic application assets | `Runic.Assets` |

Install NuGet packages with `dotnet add package <PackageId> --prerelease`; install the TypeScript runtime with `npm install @runic-artifex/application-bridge`. Use the current Application packages above for new work.

## Learn and get help

Start with the [documentation](https://github.com/Runic-Artifex/runic-toolkit/tree/main/docs) and [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples). Existing CS-WebUI consumers can use the bounded [Runic Desktop migration guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/migrate-cs-webui-to-runic-desktop.md). The Application Bridge is schema-first and deliberately has no generic property-setting or numeric-member compatibility API. Its protocol, validation, determinism, and NativeAOT guidance live in the [Application Bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md).

Report bugs or request features in [GitHub Issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Runic Toolkit is released under the [MIT License](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
