# ADR 0012: Native compiled HTML and frontend ecosystem direction

- Status: Accepted
- Date: 2026-07-26

## Context

WebUIToolkit's primary purpose is to help build applications on
[`cs-webui`](https://github.com/ViktorJannicke/cs-webui) and to provide a
practical migration path from WPF to a modern WebUI-based frontend. The
repository currently contains strong but disconnected MVVM, compiled HTML,
HTMX, browser-framework, hosting, and flow foundations.

Many target customer applications already use WPF ViewModels and a visual
language based on Bootstrap 5.3 and Font Awesome. A migration path that keeps
ViewModels, commands, validation, navigation, and most view authoring in
compiled C# is therefore strategically valuable. It is inspired by the small
native application model of Tauri combined with the typed server-side HTML
authoring of [Maud](https://maud.lambda.xyz/) and the fragment-oriented
interaction model of [HTMX](https://htmx.org/).

The WebUI C API has a custom file handler, but that handler is designed around
serving a path and returning a complete HTTP response. It does not expose the
complete method, headers, and body required to implement general HTMX form
endpoints. CsWebUi's current `WebUiVirtualFileSystem` is consequently a static
asset facility, not an application endpoint pipeline.

The maintainers also control cs-webui. The repository is expected to move into
a WebUIToolkit organization. Its virtual-file-system implementation grew from
the needs of a single customer application and has room to become an
independently designed, reusable WebUIToolkit package.

Finally, the DependencyNotices subsystem is a substantial, separately useful
product that does not advance the primary application-development or WPF
migration path.

## Decision

### Frontend authoring tracks

WebUIToolkit supports two first-class frontend tracks:

1. **Native compiled C#** uses `.cwhtml`, HTMX, WebUIToolkit MVVM, Flow, and
   CsWebUi. This is the preferred minimal-JavaScript path and the primary
   migration path for teams that want to keep UI behavior and rendering close
   to C#.
2. **Browser framework** uses the framework-neutral MVVM client with React,
   Vue, Svelte, Angular, or another browser framework. This remains available
   for applications that prefer a conventional TypeScript frontend.

The tracks share ViewModels, protocol semantics, Flow contracts, text
resources, native hosting, and operating-system capabilities. Neither track is
implemented in terms of ASP.NET Core.

### Native HTMX transport

Compiled HTML and HTMX are a native CsWebUi integration, not an embedded web
server:

- CsWebUi serves the initial document, `webui.js`, CSS, icons, and fixed
  JavaScript assets.
- `.cwhtml` compiles to Native-AOT-safe C# renderers for complete documents and
  fragments.
- A small, fixed JavaScript bridge captures HTMX-shaped requests and sends a
  bounded request envelope through one CsWebUi binding.
- The C# adapter projects that envelope onto the host-neutral HTMX runtime,
  executes the closed action or fragment route, and returns status, supported
  HTMX response headers, and rendered HTML.
- The browser bridge feeds the result back through HTMX swapping and lifecycle
  behavior.
- Host push uses CsWebUi raw messages or JavaScript dispatch to refresh or
  replace declared fragments.

The implemented transport uses a narrowly scoped `XMLHttpRequest` shim only
for toolkit-owned routes. It keeps the bridge small enough to audit and
preserves ordinary HTMX target, swap, settle, focus, and event semantics.

HTTP-shaped request and response records remain useful internal contracts, but
the native transport does not pretend to provide arbitrary HTTP hosting.

### Development and asset pipeline

Vite is the preferred optional development and production asset pipeline for
the native compiled-C# track. It owns TypeScript, JavaScript, CSS, images,
fonts, source maps, HMR, minification, content hashing, and its production
manifest. WebUIToolkit integrates it rather than building another frontend
bundler.

During development, an ephemeral loopback-only Vite server may supply assets
and HMR. It does not proxy native HTMX actions or become the application's
endpoint host. During publication, WebUIToolkit consumes Vite's deterministic
manifest and packages its output into the local static-asset/VFS boundary. The
published application contains no development-server URL and does not require
Node.js.

The cwhtml compiler owns typed application semantics and may optimize generated
HTML only where whitespace and raw-text behavior are preserved. Generated
actions, fields, routes, conversions, validation projection, render plans, and
revision handling should replace application-authored transport descriptors.
The detailed target loop and staged delivery are recorded in the
[cwhtml development-experience plan](../cwhtml-development-experience.md).

### Native security boundary

The private CsWebUi window and its native connection are the normal trust
boundary. The native HTMX transport keeps:

- closed generated route and member vocabularies;
- per-view capabilities and authoritative revisions;
- bounded frames, form values, fragments, and diagnostics;
- strict decoding and safe HTML rendering;
- deterministic session teardown.

It does not simulate Origin validation, cookies, or double-submit CSRF inside
the trusted native callback channel. Those concepts belong to a separately
named optional HTTP transport if one is introduced.

### Static assets and cs-webui ownership

`WebUiVirtualFileSystem` remains the current CsWebUi static-asset mechanism
until a replacement is designed. Static asset delivery and dynamic native
endpoint dispatch stay separate concerns.

The intended future direction is:

- move the cs-webui repository into the WebUIToolkit organization;
- extract its customer-derived virtual filesystem and deterministic asset
  packaging into a reusable WebUIToolkit-namespaced package;
- let CsWebUi consume or adapt that package instead of owning the complete
  implementation;
- design the extracted API for development directories, embedded application
  assets, safe path resolution, content types, caching, and future extension
  without coupling it to HTMX or a specific frontend framework.

The eventual package name and migration compatibility are separate design
decisions. Moving the repository does not by itself rename the established
`CsWebUi` NuGet package or namespace.

### Sample visual baseline

Desktop samples use locally packaged
[Bootstrap 5.3](https://getbootstrap.com/docs/5.3/) and
[Font Awesome](https://fontawesome.com/) by default because they match the
customer applications that motivate this toolkit.

- Samples prefer Bootstrap layout, forms, validation, navigation, dialogs, and
  accessibility conventions.
- Font Awesome supplies recognizable icons, accompanied by accessible labels
  where meaning is not otherwise available.
- Runtime CDN dependencies are not required; sample assets are pinned and
  served locally.
- Toolkit runtime and protocol packages remain CSS-framework- and
  icon-library-neutral.
- Rendering and component boundaries must leave room for shadcn, Tailwind,
  raw CSS, another design system, or consumer-owned components.

Bootstrap and Font Awesome are teaching and migration defaults, not mandatory
core dependencies.

### Repository scope

DependencyNotices will be preserved on an archival branch and removed completely
from the primary development branch, including its source projects, tests,
packages, release scenarios, build integration, and product-specific planning.
Ordinary third-party notices required to distribute WebUIToolkit and its
dependencies remain.

Command-line hosting and other supporting packages may remain optional, but
new investment is prioritized by its contribution to a complete CsWebUi
application and WPF migration path.

## Consequences

- SimpleTodo and AdvancedTodo use the native compiled-HTML transport rather
  than application-authored JavaScript callbacks.
- The compiled HTML compiler has a concrete product role rather than being a
  detached templating experiment.
- Vite supplies modern asset tooling without changing the native CsWebUi
  application boundary or becoming a production runtime dependency.
- Samples can converge on one generated application surface and shared
  frontend SDK instead of duplicating build targets and transport descriptors.
- The native HTMX and TypeScript MVVM transports become the two most important
  end-to-end integration milestones.
- Framework-neutral kernels remain reusable, but adapters must expose enough
  CsWebUi and browser capability to build real desktop applications.
- Release confidence must include a real browser-to-C# native round trip and a
  published Native-AOT application, not only fake hosts and isolated contracts.
- Static asset extraction and the cs-webui organization move can proceed
  independently from the native HTMX bridge.
