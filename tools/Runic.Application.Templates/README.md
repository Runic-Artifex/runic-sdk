# Runic.Application.Templates

Start a Runic Views Window application with React, Vue, Svelte, or Angular.

```bash
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-react --name MyApp
cd MyApp
dotnet tool restore
dotnet runic doctor
dotnet runic dev
```

Replace `react` with `vue`, `svelte`, or `angular` to select the frontend.
Requires the .NET 10 SDK and Node.js 24 with npm or pnpm, or Bun 1.4. The
templates default to npm; choose `--packageManager pnpm` or
`--packageManager bun` to use the other supported package managers. Every
generated project includes exactly one corresponding frontend lock file and a
local `dotnet-runic` tool manifest.

The generated .NET project declares a Runic Window, registered Views, and
CommunityToolkit.Mvvm view models. Views MSBuild targets generate typed
TypeScript clients and build the frontend. React and Vue consume the generated
TypeScript modules directly. Svelte uses `@runic-artifex/views-svelte`; Angular
uses `@runic-artifex/views-angular`. Both provide a typed outlet for composing
Views in the frontend. The production build embeds the frontend output with the
CS-WebUI Window adapter.

For a local candidate, install the template from the local NuGet feed. Svelte
and Angular projects also need the matching candidate npm archive through the
local `@runic-artifex` registry before installing frontend dependencies:

```bash
dotnet new install Runic.Application.Templates::<CANDIDATE> --nuget-source /path/to/nuget-feed
dotnet new runic-app-svelte --name MyCandidateApp
cd MyCandidateApp/Frontend
npm config set --location=project @runic-artifex:registry http://127.0.0.1:<PORT>
npm ci
```

`<CANDIDATE>` and the npm registry must come from the same package set. From
the generated project root, `dotnet run -- --smoke-test` runs a headless check
through the Window, View selection, and generated command.

See the [template source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Application.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
