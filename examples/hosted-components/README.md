# Hosted components

A dashboard shows two small widgets of another Runic application in its own page,
as components rather than frames
([trusted component mode](../../docs/hosted-regions.md#component-mode)). Each
widget is a process of its own with its own ViewModel; its Views render in the
dashboard's document, take the dashboard's theme and layout, and Tab moves
through them as through the page. Component mode is for applications of the same
vendor: the widget's code runs in the dashboard's page, and the page holds the
widget's token. Hosted applications are experimental: both projects suppress
[`RUNICDESK103`](../../docs/experimental.md#RUNICDESK103).

| File | What it does |
| --- | --- |
| [`Widget/Program.cs`](Widget/Program.cs) | The guest: `WidgetTool hosted <service>`. `RunicGuestOptions.AllowComponentEmbedding` allows component mode for the dashboard only; any other host gets a frame. |
| [`Widget/Frontend/vite.config.ts`](Widget/Frontend/vite.config.ts) | `vite build` with the `component` target of `@runic-artifex/vite-plugin-runic`: the widget's page, and its component in `dist/_runic_component/`. |
| [`Widget/Frontend/src/component.ts`](Widget/Frontend/src/component.ts) | The component's mount function: it renders the widget into the element's shadow root, in the element's own Runic instance. |
| [`Widget/Frontend/src/widget.ts`](Widget/Frontend/src/widget.ts) | The widget's view, shared by its own page and its component. |
| [`Widget/Angular/`](Widget/Angular/vite.config.ts) | The same widget in Angular, built by Vite with the Analog plugin ([an Angular widget](#an-angular-widget)). |
| [`Dashboard/WidgetPolicy.cs`](Dashboard/WidgetPolicy.cs) | The dashboard's policy: `EmbedComponent(region)` when `CanEmbedComponent`, otherwise `Embed(region)`. |
| [`Dashboard/Program.cs`](Dashboard/Program.cs) | The host and its page's Content-Security-Policy, which admits the widgets' origin. |
| [`Dashboard/Frontend/index.html`](Dashboard/Frontend/index.html) | Two `<runic-hosted-region>` elements, with content for each state. |

Run the dashboard. It builds the widget first:

```sh
dotnet run --project examples/hosted-components/Dashboard -c Release
```

The dashboard starts the widget twice, for the Indexer and the Sync service.
Each widget shows its process id, how long it runs and a Ping button, with the
dashboard's orange accent: the page's `--dashboard-accent` custom property
reaches the widget's shadow root. The heading of each shows how its region shows
it, such as "attached, component". Run a widget on its own:

```sh
dotnet run --project examples/hosted-components/Widget -c Release -- Indexer
```

## What each side does

- **Both opt in.** The widget lists component mode (`region-component/1`) only
  for a host it allows: its `AllowComponentEmbedding` filter compares the host's
  name, which is what the host says about itself, so it is a product filter, not
  a security boundary. The dashboard's policy then decides
  `RunicHostedWindowDecision.EmbedComponent`, which
  `RunicHostedWindowRequest.CanEmbedComponent` allows; for any other guest it
  falls back to a frame (`Embed`).
- **The component build.** `vite build` builds the widget's page and then,
  with `runic({ component: { entry, name } })`, an ES module that defines the
  widget's custom element, with the bundle's CSS as constructable stylesheets,
  its own copy of `@runic-artifex/views`, and `runic-component.json`. The .NET
  build copies both to `www/`; the widget serves `_runic_component/` only to the
  origin of the page it is attached to, with CORS, and only while attached. A
  guest without this build refuses a component attach (`embedding-unsupported`),
  and the region shows its fallback.
- **The mount function** gets the element's container, shadow root and Runic
  instance. It runs inside that instance, so the generated client connects to
  the widget's Window, never to the dashboard's page. It touches nothing of the
  dashboard's document and returns the cleanup that runs when the page removes
  the element.
- **Tags.** The build names the element `<name>-<hash of the build>`, here
  `service-widget-` and eight hex digits. The second widget loads the same build
  from another origin, its own process's, and is defined as
  `service-widget-<hash>-2`; the element takes the name from the module's `tag`
  export. Tags are prefixed in every engine. Scoped custom element registries
  would let a component keep plain names, but WebKitGTK 2.54 does not have them;
  the dashboard page reports whether its engine does (see Checks).
- **The page's policy.** The dashboard's Content-Security-Policy admits the
  widgets' origin: a component's module and its connection come from
  `127.0.0.1` on a port that changes with each run, hence
  `script-src 'self' http://127.0.0.1:*` and `connect-src 'self' ws://127.0.0.1:*`.
  That admits any local server's scripts, should the page ever run injected
  script: keep such a page free of untrusted content. `frame-src http:` covers a
  guest shown in a frame instead. The page's styles are a file of its own, since
  the policy admits no inline styles. Without the widgets' origin, the module
  does not load and the region shows its `unsupported` content.
- **In a component** the widget has the dashboard page's permissions, focus,
  drops and keys; `RunicHostedRegionOptions.Permissions` and
  `sizing="content-height"` do not apply, and the guest sends no title.

## An Angular widget

`Widget/Angular` is the widget's frontend in Angular: the same page and
component, built by Vite with the Analog Angular plugin and the `component`
target. Build the widget with it, and the dashboard shows both widgets as
Angular components:

```sh
dotnet run --project examples/hosted-components/Dashboard -c Release -p:WidgetFrontend=Angular
```

`-p:WidgetFrontend=Angular` sets `RunicApplicationFrontendDirectory` to
`Angular` in [`WidgetTool.csproj`](Widget/WidgetTool.csproj); the generated client
stays in `Frontend/src/generated`, which both frontends import.

- [`src/app/widget.ts`](Widget/Angular/src/app/widget.ts) is a standalone
  component with `injectView({ connect: connectWidget })` and its own `styles`.
  On the widget's own page ([`src/main.ts`](Widget/Angular/src/main.ts)) it
  also names the window.
- [`src/component.ts`](Widget/Angular/src/component.ts) exports
  `runicComponentMount(Widget)` from `@runic-artifex/angular/experimental`: each
  element gets an Angular application with the element's Runic instance, and
  Angular's component styles become stylesheets of the element's shadow root
  instead of `<style>` elements in the dashboard's `<head>`. The dashboard's
  policy admits no inline styles, and needs none.
- [`vite.config.ts`](Widget/Angular/vite.config.ts) adds `angular()` before
  `runic({ component })`. Its `resolve.dedupe` is for this repository only, whose
  workspace packages bring Angular of their own; a copied project takes
  `@runic-artifex/angular`, `@runic-artifex/views` and
  `@runic-artifex/vite-plugin-runic` from npm instead.
- `vite build` checks no types: the `build` script also runs
  `ngc -p tsconfig.app.json --noEmit`.

## Checks

```sh
xvfb-run -a dotnet run --project examples/hosted-components/Dashboard -c Release -- --probe
```

`--probe` is CI's check on a real desktop: the dashboard starts both widgets,
each region attaches as a component (the page imported the widget's module
under its policy and the element connected with its token), and both widgets
stop cleanly. It prints a `REGION FINDING` line with whether the engine has
scoped custom element registries: WebKitGTK 2.54 does not. CI runs it in the
native job's Linux legs, and as the elevated pair (`--probe --elevated`) in its
Windows x64 leg, whose steps run elevated: there WebView2 imports the widget's
loopback module under the page's policy.

```sh
(cd examples/hosted-components/Widget/Angular && bun install --frozen-lockfile && bun run build)
node examples/hosted-components/angular-component-smoke.mjs
```

The Angular widget's smoke runs its component in Chromium, and in WebKit when
Playwright's WebKit is installed, in a host page under the dashboard's policy,
with two origins serving the build as the two widget processes do. It checks the
build (a generated tag, no stylesheet file, Angular linked ahead of time), the
tags of both elements, the styles in each shadow root with the host page's
`--dashboard-accent`, updates and commands that reach each element's own mock
ViewModel, a host document whose `<head>`, `<html>`, title, stylesheets and
history do not change, and a teardown that leaves no stylesheet, instance or
global listener behind. CI runs it in the views job.

The example builds the Runic packages from source, and the widget's frontend
takes Vite and `@runic-artifex/vite-plugin-runic` from the repository's
workspace. To copy it, replace the `ProjectReference` and `Import` lines with
package references, as
[First Window](../first-window/README.md#copy-it-into-your-own-project) shows,
and add `vite` and `@runic-artifex/vite-plugin-runic` to the widget's
`Frontend/package.json`.
