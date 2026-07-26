# cwhtml development experience

This document defines the target inner loop and production asset pipeline for
the native compiled-C#/HTMX frontend track. It expands Phase 3 of the
[product roadmap](./product-roadmap.md) and follows the architecture boundary
in [ADR 0012](./adr/0012-native-html-and-frontend-direction.md).

## Product goal

A web developer should be able to build a CsWebUi application with typed
cwhtml, HTMX, familiar frontend assets, fast feedback, and production
optimization without learning the toolkit's transport internals or writing
custom MSBuild machinery.

The intended commands are:

```console
dotnet webuitoolkit dev
dotnet publish
```

The development command coordinates the .NET application, CsWebUi window,
cwhtml compiler, Vite development server, diagnostics, and reload behavior.
Publishing compiles the application and produces deterministic local assets
for the CsWebUi virtual filesystem. Neither command introduces ASP.NET Core.

## Implemented baseline

The repository already has the difficult runtime foundations:

- incremental cwhtml generation with source-mapped, stable diagnostics;
- Native-AOT-safe document and fragment renderers;
- typed ViewModel projection and one bounded native HTMX transport per window;
- a shared optional Vite pipeline with npm-pinned HTMX, Bootstrap 5.3, and
  Font Awesome assets served locally;
- shared MSBuild targets for cwhtml discovery, compilation, asset
  build/watch, and build/publish copying;
- `dotnet webuitoolkit dev` discovery, contract generation/verification,
  initial build, supervised Vite and .NET watchers, manifest diagnostics,
  coordinated CsWebUi restart, and clean shutdown; and
- real-browser and Native-AOT acceptance for SimpleTodo.

The sample applications no longer duplicate cwhtml compiler targets or static
asset-copy logic, and their production frontend is minified and manifest
bound. SimpleTodo now generates its HTMX descriptor/render-plan metadata and
assembles startup through the high-level native application builder.
AdvancedTodo still owns the older descriptor plumbing. The development command
currently coordinates reliable process restart; browser diagnostic overlays,
native-window asset HMR, state-preserving cwhtml replacement, richer typed
declaration syntax, and the AdvancedTodo migration remain product gaps.

## Architectural boundaries

### Vite owns frontend assets

[Vite](https://vite.dev/) is the first-class optional tool for the web asset
loop:

- TypeScript and JavaScript transformation;
- CSS, Sass, PostCSS, images, and fonts;
- dependency handling and source maps;
- CSS and JavaScript HMR during development; and
- production minification, code splitting, content hashing, and manifest
  generation.

WebUIToolkit should integrate Vite instead of implementing a competing
bundler. Applications that only use static files can omit it, and other asset
pipelines remain possible through the same manifest boundary.

### WebUIToolkit owns typed application semantics

The cwhtml compiler and runtime remain responsible for:

- typed rendering and encoding;
- actions, fields, bindings, validation, and command invocation;
- fragment identity, revision checks, and render plans;
- native CsWebUi request dispatch and host push; and
- deterministic static-HTML write coalescing.

Vite does not proxy native HTMX actions. HTMX-shaped application requests
continue to cross the private CsWebUi binding. The Vite server is an ephemeral
loopback development asset source only.

Generic post-render HTML minification is unsafe around significant whitespace,
raw-text elements, and dynamic fragments. The cwhtml compiler may coalesce
static writes and apply semantics-aware whitespace optimization, but must
preserve observable output and source diagnostics.

### Production remains local and Native-AOT-friendly

The published application contains no development URL and requires no Node.js
runtime. `dotnet publish` invokes the production frontend build when configured,
reads Vite's manifest, maps logical asset names to hashed outputs, and hands
those files to the deterministic asset/VFS package. All application assets
remain local and compatible with single-file and Native AOT publication.

## Target authoring surface

The final syntax should be proven through implementation, but the abstraction
level should resemble:

```csharp
await WebUiApp.CreateBuilder(args)
    .UseCwhtml<TodoDocumentView>()
    .UseHtmx<TodoViewModel>()
    .UseViteAssets("frontend/main.ts")
    .RunAsync();
```

A view should declare intent rather than reproduce transport metadata:

```cwhtml
form hx-post=@action(Model.AddCommand)
     hx-target="#todo-fragment"
     hx-swap="outerHTML" {
    input name=@bind(Model.NewTitle);
}
```

From those declarations, generation should supply:

- closed action and field handles;
- stable routes and member identifiers;
- form conversion and source-generated JSON obligations;
- validation association and error projection;
- affected fragments and render plans;
- revision and stale-update behavior; and
- registrations needed by the native adapter.

Generated details remain inspectable for debugging, but they are not normal
application code.

## Development loop

`dotnet webuitoolkit dev` now:

1. discover the project through `WebUIToolkit.Frontend.Sdk`;
2. runs the configured Vite asset watcher when frontend entries exist;
3. watch cwhtml and C# inputs using the same compiler configuration as build;
4. launch and monitor the CsWebUi application;
5. presents bounded, prefixed diagnostics in the terminal; and
6. performs a reliable coordinated application restart.

The loopback Vite development server, browser diagnostic overlay, and
least-disruptive update selection below are the next refinements.

Reload operates in tiers:

1. Vite applies normal CSS, JavaScript, and asset HMR without restarting .NET.
2. A compatible cwhtml-only edit replaces the generated renderer in the
   development process and asks the native bridge to refresh affected
   fragments while retaining ViewModel state.
3. An incompatible generated shape or ordinary C# edit triggers a coordinated
   application restart and browser reconnection.

Because cwhtml permits typed C# expressions, the development renderer should
use Roslyn compilation rather than a separate template interpreter whose
semantics could diverge from publish output. State-preserving replacement is a
later optimization; reliable full reload is the first delivery milestone.

An `@webuitoolkit/vite-plugin-cwhtml` package should connect the two toolchains:

- translate cwhtml diagnostics into Vite's browser overlay;
- issue custom HMR events for affected documents and fragments;
- invalidate asset and content scans when cwhtml references change; and
- expose the current generated asset manifest to the .NET development host.

## Build and package integration

`WebUIToolkit.Frontend.Sdk` should provide conventions with explicit override
points:

- automatic cwhtml discovery and compiler target import;
- standard `frontend` entry discovery;
- toolkit-owned bridge and runtime assets;
- Vite install/build invocation with locked package-manager inputs;
- development asset URL and production manifest generation;
- deterministic copying or embedding into the VFS input; and
- extension points for custom Vite configuration and alternative pipelines.

Sample and consumer projects should not contain `UsingTask`, compiler target,
bridge-copy, or package-directory probing boilerplate. The SDK must expose
clear errors for a missing Node installation, stale lock file, invalid
manifest, duplicate logical asset, or development server that cannot start.

## Editor experience

The browser overlay is not a substitute for editor tooling. After the build and
watch path stabilizes, provide a cwhtml language server and editor extension
with:

- syntax highlighting and formatting;
- HTML, HTMX, Bootstrap, and Font Awesome completion where applicable;
- C# expression completion and signature help;
- immediate compiler diagnostics;
- go to definition, find references, and safe rename;
- navigation between actions, fields, ViewModels, and fragments; and
- generated C# and rendered-fragment inspection.

The command line, browser overlay, build, and editor must report the same
diagnostic identifiers and source spans.

## Delivery order

1. **Implemented:** package shared cwhtml targets and integrate them with
   `WebUIToolkit.Frontend.Sdk`; remove duplicated sample MSBuild.
2. **Implemented for SimpleTodo:** introduce the high-level application
   builder; migrate AdvancedTodo next.
3. **Partially implemented:** generate field, action, command, fragment,
   focus, event, and render-plan plumbing from cwhtml. Conversion and
   validation completion is explicit and AdvancedTodo remains to migrate.
4. **Implemented:** integrate Vite production builds and asset-manifest
   generation/copying.
5. **Partially implemented:** add `dotnet webuitoolkit dev` and reliable
   coordinated full reload. Asset HMR and the browser diagnostics overlay
   remain.
6. Add the cwhtml language server and editor integration.
7. Add compatible renderer replacement and state-preserving fragment refresh.
8. Build project templates, reusable cwhtml components, and scaffolding on the
   stable authoring surface.

The first three steps deliberately remove application boilerplate before more
samples are expanded. Samples should exercise the consumer experience, not
serve as permanent build-system fixtures.

## Acceptance criteria

Phase 3 is complete when:

- a new native cwhtml application can start development and publish with one
  documented command each;
- SimpleTodo and AdvancedTodo have no application-owned cwhtml build targets,
  toolkit asset-copy targets, native-HTMX descriptors, or render-plan
  registration;
- CSS and JavaScript changes update without restarting the .NET application;
- cwhtml edits produce either a correct fragment refresh or a reliable
  coordinated reload, with state preservation for the supported edit class;
- terminal, browser, build, and editor diagnostics agree on identifiers and
  source locations;
- production JavaScript and CSS are minified and content-hashed, manifest
  output is deterministic, and no development URL is present;
- the published application works offline through the local VFS and retains
  full-trim and Native-AOT compatibility; and
- custom Vite configuration and a no-Vite static-assets path both remain
  supported.

Warm HMR and cwhtml feedback latency should be measured in CI on a declared
reference application before numeric performance budgets are frozen. The
initial target is sub-second cwhtml feedback and asset HMR fast enough to feel
instantaneous on a normal development workstation.
