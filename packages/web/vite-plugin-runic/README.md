# `@runic-artifex/vite-plugin-runic`

Vite 8 integration for Runic Desktop bootstrap, development diagnostics, and
optional Vite DevTools. The .NET Views build owns generated TypeScript clients;
this plugin does not generate application contracts.

```ts
import { defineConfig } from "vite";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({ plugins: [runic({ desktop: true })] });
```

`desktop: true` inserts the Desktop bootstrap script and sets a relocatable
production base. For a CS-WebUI frontend, use `runic({ devtools: false })` or
omit the plugin entirely. A Runic Views frontend needs neither: its page loads
`runic-views.js`, the Views client that every host provides, so `runic()` with
`base: "./"` builds one page for Runic Desktop and CS-WebUI. The Views build
passes the host it builds for as `RUNIC_APPLICATION_HOST` for a configuration
that still differs per host. `virtual:runic/client` and the `/client` entry point
provide bounded diagnostics and HMR resource ownership helpers. The optional
`@vitejs/devtools` peer enables the Runic dock; register `DevTools()` in your
Vite configuration when selecting `devtools: true`.

`devtools` takes `true`, `false` or `"auto"`. `"auto"` is the default and the
same as omitting the option: the development client is injected while serving,
and the Runic dock appears when `DevTools()` is registered. `true` also requires
`DevTools()`: serving without it fails with
[`RUNICP007`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicp007).
`false` turns off both the dock and the development client.
`maxTimelineEntries` sets how many timeline entries the dock keeps: 200 by
default, rounded down and limited to 1 to 500.

While serving, the plugin adds `virtual:runic/client` to `index.html`. It
forwards `@runic-artifex/views` failures to the timeline: the failed route, the
error kind and, for errors, a failure with the exception type, message and
stack. .NET includes that detail only in development (see the
`IncludeFailureDetail` window option of Runic.Application.Views). The **Last failure** card of the
dock shows the most recent one. A failure keeps file paths, so that a stack is
useful, but is bounded and has credential values redacted. Failures stay in
the dock: `/__runic/state` and the copied diagnostic state leave them out, and
the other timeline fields stay path-free. An application without a Vite-served `index.html` imports
`virtual:runic/client` itself. `devtools: false` disables the injection.
The plugin adds `@runic-artifex/vite-plugin-runic/client` to
`optimizeDeps.include`, because the dependency scanner cannot see the injected
client: with a cold `node_modules/.vite` cache, the first page would otherwise
discover it, and Vite would reload the page and close the host's connection.

The native host serves `webui.js` and its client script (`runic-desktop.js`,
`runic-views.js`, `runic-cswebui.js` or `runic-desktop-views.js`) from the root
of the web content, outside the Vite build. The plugin leaves those classic script tags
unbundled, so `vite build` prints no "can't be bundled without
type="module"" warning, and points them at the web root from every page: a
nested page such as `pages/settings.html` loads `../webui.js` in the build and
during `dotnet runic dev`.
