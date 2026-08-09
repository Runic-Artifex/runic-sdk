![Runic Artifex banner](.github/assets/brand/banner.png)

# Runic Vite

First-class Vite 8 development integration for Runic Toolkit applications.

```ts
import { DevTools } from "@vitejs/devtools";
import { runicToolkit } from "@runic-artifex/vite-plugin-runic-toolkit";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runicToolkit(),
  ],
});
```

Import the virtual client once from the browser entry. This is required for
native/backend-hosted HTML and also supplies the HMR-persistent resource and
sanitized bridge-observer helpers.

```ts
import {
  createRunicToolkitDevtoolsObserver,
  preserveRunicToolkitHmrResource,
} from "virtual:runic-toolkit/client";
```

The plugin contributes an official Vite DevTools dock with Application Bridge
connection, contract, operation, and sanitized timeline state. Runtime payloads
are bounded and discard secrets, capabilities, native paths, raw frames, and
stack traces. No DevTools client is included in production output.
