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
omit the plugin entirely. `virtual:runic/client` and the `/client` entry point
provide bounded diagnostics and HMR resource ownership helpers. The optional
`@vitejs/devtools` peer enables the Runic dock; register `DevTools()` in your
Vite configuration when selecting `devtools: true`.
